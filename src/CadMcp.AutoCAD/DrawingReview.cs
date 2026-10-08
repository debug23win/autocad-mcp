using System.Text.Json;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Core;
using App = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CadMcp.AutoCAD;

internal static class DrawingReview
{
    public sealed class PreviewView : IDisposable
    {
        private readonly Document doc;
        private readonly ViewTableRecord original;
        private readonly bool changed;
        public PreviewView(Document document, Transaction tr, JsonElement options, int width, int height)
        {
            doc = document; original = doc.Editor.GetCurrentView();
            try
            {
            string name = options.Text("view_name") ?? "current";
            Vector3d? direction = name switch { "current" => null, "front" => new(0,-1,0), "back" => new(0,1,0),
                "left" => new(-1,0,0), "right" => new(1,0,0), "top" => Vector3d.ZAxis, "isometric" => new(1,-1,1),
                _ => throw new CadFault("INVALID_VIEW", "Use current/front/back/left/right/top/isometric") };
            if (options.Text("view_direction_json") is { } vector)
            {
                using var json=JsonDocument.Parse(vector); var p=EditPlan.Point(json.RootElement); direction=new(p[0],p[1],p[2]);
                if(direction.Value.Length<1e-10) throw new CadFault("INVALID_VIEW","View direction must be nonzero");
            }
            Extents3d? bounds = null;
            if(options.Text("handles_json") is { } handlesJson)
            {
                using var handles=JsonDocument.Parse(handlesJson);
                if(handles.RootElement.ValueKind!=JsonValueKind.Array || handles.RootElement.GetArrayLength() is <1 or >500) throw new CadFault("INVALID_HANDLES","Frame 1..500 handles");
                foreach(var h in handles.RootElement.EnumerateArray())
                {
                    if(!long.TryParse(h.GetString(),System.Globalization.NumberStyles.HexNumber,null,out long value) || !doc.Database.TryGetObjectId(new Handle(value),out var id) || id.IsErased)
                        throw new CadFault("ENTITY_NOT_FOUND","Cannot frame the requested entity");
                    if(tr.GetObject(id,OpenMode.ForRead) is not Entity entity) throw new CadFault("INVALID_HANDLES","Only entity handles can be framed");
                    var e=entity.GeometricExtents;
                    if(bounds is null) bounds=e; else { var combined=bounds.Value; combined.AddExtents(e); bounds=combined; }
                }
            }
            changed=direction.HasValue || bounds.HasValue;
            if(!changed)return;
            using var view=doc.Editor.GetCurrentView();
            view.ViewDirection=(direction ?? original.ViewDirection).GetNormal(); view.ViewTwist=0; view.PerspectiveEnabled=false;
            if(bounds is { } box)
            {
                var target=new Point3d((box.MinPoint.X+box.MaxPoint.X)/2,(box.MinPoint.Y+box.MaxPoint.Y)/2,(box.MinPoint.Z+box.MaxPoint.Z)/2);
                view.Target=target; view.CenterPoint=Point2d.Origin;
                // Project corners with AutoCAD's actual WCS->DCS transform.
                var worldToDcs=(Matrix3d.Displacement(target-Point3d.Origin)*Matrix3d.PlaneToWorld(view.ViewDirection)).Inverse();
                var corners=(from x in new[]{box.MinPoint.X,box.MaxPoint.X} from y in new[]{box.MinPoint.Y,box.MaxPoint.Y} from z in new[]{box.MinPoint.Z,box.MaxPoint.Z}
                    select new Point3d(x,y,z).TransformBy(worldToDcs)).ToArray();
                double w=Math.Max(1e-6,corners.Max(p=>p.X)-corners.Min(p=>p.X))*1.15;
                double h=Math.Max(1e-6,corners.Max(p=>p.Y)-corners.Min(p=>p.Y))*1.15;
                view.Width=Math.Max(w,h*width/height); view.Height=view.Width*height/width;
            }
            doc.Editor.SetCurrentView(view); doc.Editor.Regen();
            }
            catch
            {
                try { if (changed) doc.Editor.SetCurrentView(original); } catch { }
                original.Dispose(); throw;
            }
        }
        public void Dispose() { try { if(changed) doc.Editor.SetCurrentView(original); } catch(System.Exception e) { System.Diagnostics.Trace.WriteLine(e.Message); } finally { original.Dispose(); } }
    }
    public static object DocumentState(Document doc)
    {
        try
        {
            if(!ReferenceEquals(doc,App.DocumentManager.MdiActiveDocument))return new {name=doc.Name,disk_save="unknown",evidence="DBMOD is only reliable for active drawing",automatic_save=false};
            int dbmod = Convert.ToInt32(App.GetSystemVariable("DBMOD"));
            var file = new FileInfo(doc.Name);
            bool exists = Path.IsPathRooted(doc.Name) && file.Exists;
            return new { name = doc.Name, dbmod, disk_save = exists && dbmod == 0 ? "saved" : "unsaved",
                file_exists = exists, bytes = exists ? (long?)file.Length : null,
                last_write_utc = exists ? (DateTimeOffset?)file.LastWriteTimeUtc : null,
                evidence = "AutoCAD_DBMOD_and_current_file_metadata", automatic_save = false };
        }
        catch (System.Exception error) { return new { name = doc.Name, disk_save = "unknown", error = error.Message, automatic_save = false }; }
    }
    public static object Verify(Document doc, Transaction tr, JsonElement options, OperationJournal journal)
    {
        var handles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new Dictionary<string, string>();
        JsonElement expectations = DrawingVerification.Parse(options.Text("expectations_json"));
        if (options.Text("operation_id") is { } operation)
        {
            var entry = journal.Find(operation) ?? throw new CadFault("OPERATION_NOT_FOUND", "No receipt: provide explicit handles and reconcile the drawing");
            if (entry.DocumentId != options.Text("verified_document_id")) throw new CadFault("DOCUMENT_MISMATCH", "Operation belongs to another document");
            if (entry.Result is null) throw new CadFault("OPERATION_PENDING", "Wait for the operation receipt before verifying its entities");
            var outer = Wire.Element(entry.Result.Data ?? new { });
            var result = outer.TryGetProperty("result", out var r) ? r : outer;
            // A large edit lists every changed handle separately from its shortened readback.
            if (result.TryGetProperty("changed_handles", out var changed) && changed.ValueKind == JsonValueKind.Array)
            { foreach (var h in changed.EnumerateArray()) if (h.ValueKind == JsonValueKind.String && h.GetString() is { } handle) handles.Add(handle); }
            else if (result.TryGetProperty("changed_handles_omitted", out var omitted) && omitted.ValueKind == JsonValueKind.True)
                throw new CadFault("VERIFY_TOO_LARGE", "The operation changed too many entities to list; verify them in parts with handles_json (up to 500)");
            else if (result.TryGetProperty("entities", out var entities)) foreach (var e in entities.EnumerateArray()) if (e.Text("handle") is { } handle) handles.Add(handle);
            if (handles.Count > 500) throw new CadFault("VERIFY_TOO_LARGE", "The operation changed " + handles.Count + " entities; verify them in parts with handles_json (up to 500)");
            if (result.TryGetProperty("results", out var records)) foreach (var record in records.EnumerateArray())
                if (record.Text("id") is { } alias && record.Text("handle") is { } handle) aliases[alias] = handle;
            if (options.Text("expectations_json") is null) expectations = DrawingVerification.Parse(entry.Request?.Data.Text("expectations_json"));
        }
        if (options.Text("handles_json") is { } json)
        {
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Array || parsed.RootElement.GetArrayLength() is < 1 or > 500)
                throw new CadFault("INVALID_HANDLES", "Provide 1..500 entity handles");
            foreach (var h in parsed.RootElement.EnumerateArray()) handles.Add(h.GetString() ?? throw new CadFault("INVALID_HANDLES", "Expected hexadecimal handle strings"));
        }
        if (handles.Count == 0) throw new CadFault("NO_VERIFICATION_TARGETS", "Provide handles or a completed native edit operation_id");
        if (handles.Count > 500) throw new CadFault("INVALID_HANDLES", "At most 500 entities per review");
        var readback = handles.Select(handle => {
            if (!long.TryParse(handle, System.Globalization.NumberStyles.HexNumber, null, out long value)) throw new CadFault("INVALID_HANDLE", handle);
            if (!doc.Database.TryGetObjectId(new Handle(value), out var id)) return Wire.Element(new { handle, missing = true, erased = true });
            var entity = tr.GetObject(id, OpenMode.ForRead, true) as Entity;
            return entity is null || entity.IsErased ? Wire.Element(new { handle, erased = true }) : Reader.Read(entity, tr);
        }).ToArray();
        return new { verification = DrawingVerification.Evaluate(readback, doc.Database.Insunits.ToString(), expectations, aliases),
            entities = readback, document_state = DocumentState(doc), visual_fidelity = "requires_comparable_render_and_reference_review",
            checked_at = DateTimeOffset.UtcNow, source = "current_AutoCAD_database" };
    }
}
