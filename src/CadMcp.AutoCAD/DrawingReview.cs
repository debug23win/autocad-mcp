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
    public static object DocumentState(Document doc)
    {
        try
        {
            if(!ReferenceEquals(doc,App.DocumentManager.MdiActiveDocument))return new {name=doc.Name,disk_save="unknown",evidence="DBMOD is only reliable for active drawing",automatic_save=AutoSaver.Summary(doc)};
            int dbmod = Convert.ToInt32(App.GetSystemVariable("DBMOD"));
            var file = new FileInfo(doc.Name);
            bool exists = Path.IsPathRooted(doc.Name) && file.Exists;
            return new { name = doc.Name, dbmod, disk_save = exists && dbmod == 0 ? "saved" : "unsaved",
                file_exists = exists, bytes = exists ? (long?)file.Length : null,
                last_write_utc = exists ? (DateTimeOffset?)file.LastWriteTimeUtc : null,
                evidence = "AutoCAD_DBMOD_and_current_file_metadata", automatic_save = AutoSaver.Summary(doc) };
        }
        catch (System.Exception error) { return new { name = doc.Name, disk_save = "unknown", error = error.Message, automatic_save = AutoSaver.Summary(doc) }; }
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
