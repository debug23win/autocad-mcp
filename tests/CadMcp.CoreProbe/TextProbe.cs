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
    internal static void CheckRegression(Document doc, Action<bool, string> assert, string? dynamicFixture)
    {
        var db = doc.Database;
        JsonElement Edit(object plan) => Wire.Element(Edits.Execute(doc, EditPlan.Parse(JsonSerializer.Serialize(plan)), default));
        string[] handles;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
            var model = (BlockTableRecord)tr.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            var paper = (BlockTableRecord)tr.GetObject(blocks[BlockTableRecord.PaperSpace], OpenMode.ForWrite);
            BlockTableRecord Definition(string name)
            {
                var block = new BlockTableRecord { Name = name };
                blocks.Add(block); tr.AddNewlyCreatedDBObject(block, true); return block;
            }
            var a = Definition("CADMCP_PARAGRAPH_A");
            var b = Definition("CADMCP_PARAGRAPH_B");
            BlockTableRecord[] owners = [model, paper, a, b, model, model];
            handles = owners.Select((owner, i) =>
            {
                var text = new DBText { Position = new Point3d(10000, 100 - i * 3.5, i >= 4 ? 100 : 0),
                    TextString = "Line " + i, Height = 2.5, TextStyleId = db.Textstyle };
                owner.AppendEntity(text); tr.AddNewlyCreatedDBObject(text, true); return text.Handle.ToString();
            }).ToArray();
            tr.Commit();
        }
        using (var tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            var read = Wire.Element(TextUnits.Read(db, tr, Wire.Element(new { scope = "all", include = "text,block_definitions" }), default));
            var units = read.GetProperty("units").EnumerateArray().Select(u => u.Text("unit")!).Where(u => u.Split('+').Any(handles.Contains)).ToHashSet();
            assert(units.SetEquals(new[] { handles[0], handles[1], handles[2], handles[3], handles[4] + "+" + handles[5] }),
                "scope all keeps model, paper, block definitions and different Z separate while grouping valid stacked lines");
        }
        foreach (var invalid in new[] { handles[0] + "+" + handles[1], handles[2] + "+" + handles[3], handles[0] + "+" + handles[4] })
        {
            bool rejected = false;
            try { Edit(new object[] { new { op = "text_translate", units = new[] { new { unit = invalid, text = "Must not be written" } } } }); }
            catch (CadFault error) when (error.Code == "INVALID_UNIT") { rejected = true; }
            assert(rejected, "Legacy cross-space or cross-elevation paragraph is rejected: " + invalid);
        }
        using (var tr = db.TransactionManager.StartOpenCloseTransaction())
            assert(handles.Select((h, i) => ((DBText)tr.GetObject(NativeTables.Resolve(db, h), OpenMode.ForRead)).TextString == "Line " + i).All(v => v),
                "Rejected paragraph translations leave every original TEXT unchanged");

        if (string.IsNullOrWhiteSpace(dynamicFixture)) throw new InvalidOperationException("A local dynamic-block DWG fixture is required for this regression check");
        // An installed sample is read only and cloned into the disposable test drawing; no Autodesk DWG is redistributed.
        using var source = new Database(false, true);
        source.ReadDwgFile(dynamicFixture, FileOpenMode.OpenForReadAndAllShare, true, null);
        ObjectId sourceBlock;
        using (var tr = source.TransactionManager.StartOpenCloseTransaction())
            sourceBlock = ((BlockTable)tr.GetObject(source.BlockTableId, OpenMode.ForRead)).Cast<ObjectId>()
                .First(id => tr.GetObject(id, OpenMode.ForRead) is BlockTableRecord { IsDynamicBlock: true, IsAnonymous: false, IsLayout: false });
        var mapping = new IdMapping();
        source.WblockCloneObjects(new ObjectIdCollection(new[] { sourceBlock }), db.BlockTableId, mapping, DuplicateRecordCloning.Ignore, false);
        var definitionId = mapping[sourceBlock].Value;
        ObjectId[] references;
        string blockName;
        const string oldText = "CADMCP_DYNAMIC_OLD", newText = "CADMCP_DYNAMIC_NEW";
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForWrite);
            blockName = definition.Name;
            var text = new DBText { Position = Point3d.Origin, Height = 2.5, TextString = oldText, TextStyleId = db.Textstyle };
            definition.AppendEntity(text); tr.AddNewlyCreatedDBObject(text, true);
            definition.UpdateAnonymousBlocks();
            var model = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            references = Enumerable.Range(0, 2).Select(i =>
            {
                var reference = new BlockReference(new Point3d(12000 + i * 500, 0, 0), definitionId);
                model.AppendEntity(reference); tr.AddNewlyCreatedDBObject(reference, true); return reference.ObjectId;
            }).ToArray();
            var changed = (BlockReference)tr.GetObject(references[1], OpenMode.ForWrite);
            foreach (DynamicBlockReferenceProperty property in changed.DynamicBlockReferencePropertyCollection)
            {
                if (property.ReadOnly) continue;
                object? value = property.GetAllowedValues().FirstOrDefault(v => !Equals(v, property.Value));
                if (value is null && property.Value is double number) value = number + Math.Max(10, Math.Abs(number) * 0.1);
                if (value is null) continue;
                property.Value = value;
                if (changed.BlockTableRecord != definitionId) break;
            }
            assert(changed.BlockTableRecord != definitionId, "Dynamic fixture produces a modified anonymous block reference");
            tr.Commit();
        }
        string[] State()
        {
            using var tr = db.TransactionManager.StartOpenCloseTransaction();
            return references.Select(id => string.Join(";", ((BlockReference)tr.GetObject(id, OpenMode.ForRead)).DynamicBlockReferencePropertyCollection
                .Cast<DynamicBlockReferenceProperty>().Select(p => p.PropertyName + "=" + Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture)))).ToArray();
        }
        var before = State();
        var result = Edit(new object[] { new { op = "text_replace", find = oldText, replace = newText, include = new[] { "text", "block_definitions" } } });
        var detail = result.GetProperty("results")[0].GetProperty("detail");
        assert(detail.GetProperty("dynamic_blocks_updated").EnumerateArray().Any(v => v.GetString() == blockName)
            && (!detail.TryGetProperty("dynamic_blocks_not_updated", out var failed) || failed.ValueKind == JsonValueKind.Null), "text_replace reports the rebuilt dynamic definition");
        using (var tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            foreach (var id in references)
            {
                var reference = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
                var displayed = ((BlockTableRecord)tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead)).Cast<ObjectId>()
                    .Select(id => tr.GetObject(id, OpenMode.ForRead)).OfType<DBText>().Select(t => t.TextString).ToArray();
                assert(displayed.Contains(newText) && !displayed.Contains(oldText), "Native dynamic insert displays the replaced text: " + reference.Handle);
            }
        }
        assert(before.SequenceEqual(State()), "Dynamic reference parameter values are preserved after replacing definition text");
    }

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
