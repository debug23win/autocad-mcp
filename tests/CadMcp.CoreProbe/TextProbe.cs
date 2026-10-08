using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Core;
using CadMcp.AutoCAD;

namespace CadMcp.CoreProbe;

/// <summary>
/// Native checks of polyline corner rounding, translation units, text translation and fitting (TEXT paragraphs, MTEXT,
/// multiline attributes, locked layers), and the proxy and property reads. Every result is read back from the database.
/// </summary>
internal static class TextProbe
{
    internal static void Check(Document doc, Action<bool, string> assert)
    {
        var db = doc.Database;
        JsonElement Edit(object plan) => Wire.Element(Edits.Execute(doc, EditPlan.Parse(JsonSerializer.Serialize(plan)), default));
        JsonElement Read(Func<Transaction, object> read) { using var tr = db.TransactionManager.StartOpenCloseTransaction(); return Wire.Element(read(tr)); }
        JsonElement Units(params string[] handles) => Read(tr => TextUnits.Read(db, tr, Wire.Element(new { handles_json = JsonSerializer.Serialize(handles) }), default));
        string Handle(JsonElement result, string alias) => result.GetProperty("results").EnumerateArray().First(r => r.Text("id") == alias).Text("handle")!;
        T Open<T>(Transaction tr, string handle) where T : DBObject => (T)tr.GetObject(NativeTables.Resolve(db, handle), OpenMode.ForRead);

        // Corner rounding: a 20 x 10 rectangle with radius 2 loses 16 of straight length and gains four quarter circles.
        var shapes = Edit(new object[]
        {
            new { op = "rectangle", id = "frame", first = new[] { 5000, 0 }, second = new[] { 5020, 10 } },
            new { op = "text", id = "l1", position = new[] { 5000, 100 }, text = "Plan of the", height = 2.5 },
            new { op = "text", id = "l2", position = new[] { 5000, 96.5 }, text = "ground floor", height = 2.5 },
            new { op = "text", id = "l3", position = new[] { 5000, 93 }, text = "of the building", height = 2.5 }
        });
        string frame = Handle(shapes, "frame");
        Edit(new object[] { new { op = "polyline_fillet", handle = frame, radius = 2 } });
        using (var tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            var polyline = Open<Polyline>(tr, frame);
            double quarter = Math.Tan(Math.PI / 8);
            assert(polyline.NumberOfVertices == 8 && Enumerable.Range(0, 8).Count(i => Math.Abs(Math.Abs(polyline.GetBulgeAt(i)) - quarter) < 1e-9) == 4
                && Math.Abs(polyline.Length - (60 - 16 + 4 * Math.PI)) < 1e-8, "polyline_fillet rounds the four rectangle corners with quarter arcs of the native polyline");
        }

        // Stacked TEXT lines are one unit; the translation rewraps across the same lines.
        string[] lines = [Handle(shapes, "l1"), Handle(shapes, "l2"), Handle(shapes, "l3")];
        var paragraph = Units(lines).GetProperty("units").EnumerateArray().Single();
        assert(paragraph.Text("kind") == "paragraph" && paragraph.Text("unit") == string.Join("+", lines) && paragraph.Text("text") == "Plan of the ground floor of the building",
            "Three stacked TEXT lines read as one paragraph unit from top to bottom");
        const string Translation = "План первого этажа здания";
        Edit(new object[] { new { op = "text_translate", units = new[] { new { unit = paragraph.Text("unit"), text = Translation, source = paragraph.Text("text") } } } });
        using (var tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            var texts = lines.Select(h => Open<DBText>(tr, h)).ToArray();
            assert(string.Join(" ", texts.Select(t => t.TextString.Trim()).Where(t => t.Length > 0)) == Translation && texts.All(t => t.WidthFactor <= 1 + 1e-9 && t.Height <= 2.5 + 1e-9),
                "text_translate writes the paragraph across its native TEXT lines without growing them");
        }

        // MTEXT keeps the formatting that opens it; a multiline attribute keeps its translation and fitted width.
        string mtextHandle, attributeHandle, referenceHandle, lockedHandle;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
            var model = (BlockTableRecord)tr.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            var mtext = new MText { Location = new Point3d(5000, 200, 0), Contents = "{\\C1;Hello world}", TextHeight = 2.5, Width = 40 };
            model.AppendEntity(mtext); tr.AddNewlyCreatedDBObject(mtext, true);
            mtextHandle = mtext.Handle.ToString();

            var block = new BlockTableRecord { Name = "CADMCP_NOTE" };
            blocks.Add(block); tr.AddNewlyCreatedDBObject(block, true);
            var definition = new AttributeDefinition { Tag = "NOTE", Prompt = "NOTE", TextString = "Old note", Position = Point3d.Origin, Height = 2.5, TextStyleId = db.Textstyle };
            definition.Justify = AttachmentPoint.TopLeft; definition.AlignmentPoint = Point3d.Origin;
            block.AppendEntity(definition); tr.AddNewlyCreatedDBObject(definition, true);
            // The same order as the SPDS title block: convert once it belongs to the transaction, then set the MText back.
            definition.IsMTextAttributeDefinition = true;
            definition.UpdateMTextAttributeDefinition();
            using (var contents = definition.MTextAttributeDefinition)
            {
                contents.Location = Point3d.Origin; contents.Width = 40; contents.TextHeight = 2.5; contents.Contents = "Old note"; contents.Attachment = AttachmentPoint.TopLeft;
                definition.MTextAttributeDefinition = contents;
            }
            var reference = new BlockReference(new Point3d(5100, 200, 0), block.ObjectId);
            model.AppendEntity(reference); tr.AddNewlyCreatedDBObject(reference, true);
            var attribute = new AttributeReference();
            attribute.SetAttributeFromBlock(definition, reference.BlockTransform);
            reference.AttributeCollection.AppendAttribute(attribute); tr.AddNewlyCreatedDBObject(attribute, true);
            attributeHandle = attribute.Handle.ToString(); referenceHandle = reference.Handle.ToString();

            var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);
            var locked = new LayerTableRecord { Name = "CADMCP_LOCKED_TEXT" };
            layers.Add(locked); tr.AddNewlyCreatedDBObject(locked, true);
            var fixedText = new DBText { Position = new Point3d(5000, 300, 0), TextString = "Locked", Height = 2.5, LayerId = locked.ObjectId };
            model.AppendEntity(fixedText); tr.AddNewlyCreatedDBObject(fixedText, true);
            locked.IsLocked = true;
            lockedHandle = fixedText.Handle.ToString();
            tr.Commit();
        }
        var mtextUnit = Units(mtextHandle).GetProperty("units").EnumerateArray().Single();
        assert(mtextUnit.Text("kind") == "mtext" && mtextUnit.Text("text") == "Hello world", "MTEXT reads as its displayed text without formatting codes");
        var attributeUnit = Units(referenceHandle).GetProperty("units").EnumerateArray().Single();
        assert(attributeUnit.Text("kind") == "attribute_mtext" && attributeUnit.Text("unit") == attributeHandle && attributeUnit.Text("text") == "Old note",
            "A multiline attribute reads as its own unit through its block reference");
        var lockedUnit = Units(lockedHandle).GetProperty("units").EnumerateArray().Single();
        assert(lockedUnit.TryGetProperty("locked", out var lockedFlag) && lockedFlag.ValueKind == JsonValueKind.True, "A TEXT on a locked layer reads as locked");

        var written = Edit(new object[]
        {
            new { op = "text_translate", units = new object[]
            {
                new { unit = mtextHandle, text = "Привет мир", source = "Hello world" },
                new { unit = attributeHandle, text = "Новая заметка", source = "Old note" },
                new { unit = lockedHandle, text = "Заблокировано", source = "Locked" }
            } },
            new { op = "text_fit", handle = attributeHandle, width = 25 }
        });
        var translate = written.GetProperty("results")[0].GetProperty("detail");
        assert(translate.GetProperty("translated").GetInt32() == 2 && translate.GetProperty("skipped").EnumerateArray().Single().Text("reason") == "locked_layer",
            "A unit on a locked layer is skipped while the rest of the batch is written");
        using (var tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            assert(Open<MText>(tr, mtextHandle).Contents == "{\\C1;Привет мир}", "MTEXT translation keeps its opening colour code");
            var attribute = Open<AttributeReference>(tr, attributeHandle);
            using var content = attribute.MTextAttribute;
            assert(attribute.IsMTextAttribute && CadText.Normalize(content.Contents, true) == "Новая заметка" && Math.Abs(content.Width - 25) < 1e-9,
                "A multiline attribute keeps its translated contents and fitted column width: " + content.Contents + ", " + content.Width);
            assert(Open<DBText>(tr, lockedHandle).TextString == "Locked", "TEXT on a locked layer is left unchanged");
        }

        // Reads of proxies and of the Properties palette answer without changing the drawing.
        var proxies = Read(tr => ObjectInspection.Proxies(db, tr, Wire.Element(new { }), default));
        assert(proxies.GetProperty("handles_scanned").GetInt64() > 0 && proxies.GetProperty("groups").ValueKind == JsonValueKind.Array, "Proxy scan walks every handle of the native drawing");
        var properties = Read(tr => ObjectInspection.Properties(db, tr, Wire.Element(new { handles_json = JsonSerializer.Serialize(new[] { frame }) }), default));
        var described = properties.GetProperty("objects")[0];
        assert(described.Text("type") == "Polyline" && described.GetProperty("palette").TryGetProperty("error", out _), "Property read names the object and reports that the palette needs full AutoCAD");
    }
}
