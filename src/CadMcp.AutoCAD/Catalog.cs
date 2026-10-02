using Autodesk.AutoCAD.DatabaseServices;

namespace CadMcp.AutoCAD;

internal static class Catalog
{
    public static object Read(Database db, Transaction tr)
    {
        const int limit = 500;
        var layers = ((LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead)).Cast<ObjectId>().Select(id => (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead)).ToArray();
        var blocks = ((BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead)).Cast<ObjectId>().Select(id => (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).Where(b => !b.IsLayout).ToArray();
        var styles = ((TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead)).Cast<ObjectId>().Select(id => (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead)).ToArray();
        var dimensionStyles = ((DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead)).Cast<ObjectId>().Select(id => (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead)).ToArray();
        var types = ((LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead)).Cast<ObjectId>().Select(id => (LinetypeTableRecord)tr.GetObject(id, OpenMode.ForRead)).ToArray();
        var layoutDictionary = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        var layouts = layoutDictionary.Cast<System.Collections.DictionaryEntry>().Select(x => x.Value is ObjectId id
            ? (Layout)tr.GetObject(id, OpenMode.ForRead) : throw new InvalidOperationException("Invalid layout dictionary entry")).OrderBy(x => x.TabOrder).ToArray();
        var tableStyles = ((DBDictionary)tr.GetObject(db.TableStyleDictionaryId, OpenMode.ForRead)).Cast<System.Collections.DictionaryEntry>().Select(e => e.Key.ToString()).ToArray();
        // Return copied scalar data only. Deferred projections would retain open native
        // database objects and touch them again during serialization on the pipe thread.
        return new { table_styles = tableStyles.Take(limit).ToArray(), layers = layers.Take(limit).Select(l => new { name = l.Name, color_index = l.Color.ColorIndex, locked = l.IsLocked, off = l.IsOff, frozen = l.IsFrozen, dependent = l.IsDependent }).ToArray(),
            blocks = blocks.Take(limit).Select(b => new { name = b.Name, handle = b.Handle.ToString(), xref = b.IsFromExternalReference, dependent = b.IsDependent,
                attributes = b.Cast<ObjectId>().Select(id => tr.GetObject(id, OpenMode.ForRead)).OfType<AttributeDefinition>().Take(100).Select(a => new { tag = a.Tag, text = a.TextString, constant = a.Constant }).ToArray() }).ToArray(),
            text_styles = styles.Take(limit).Select(s => new { name = s.Name, font = s.FileName, height = s.TextSize }).ToArray(),
            dimension_styles = dimensionStyles.Take(limit).Select(s => s.Name).ToArray(), linetypes = types.Take(limit).Select(t => t.Name).ToArray(),
            layouts = layouts.Take(limit).Select(l => LayoutInfo(l, tr)).ToArray(),
            truncated = layers.Length > limit || blocks.Length > limit || styles.Length > limit || dimensionStyles.Length > limit || types.Length > limit || layouts.Length > limit, limit_per_category = limit };
    }

    private static object LayoutInfo(Layout layout, Transaction tr)
    {
        var space = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
        var objects = space.Cast<ObjectId>().Take(1001).Select(id => tr.GetObject(id, OpenMode.ForRead)).ToArray();
        return new { name = layout.LayoutName, handle = layout.Handle.ToString(), model = layout.ModelType,
            tab_order = layout.TabOrder, selected = layout.TabSelected, plot_device = layout.PlotConfigurationName,
            paper_size = layout.CanonicalMediaName, paper_units = layout.PlotPaperUnits.ToString(),
            paper_rotation = layout.PlotRotation.ToString(), plot_style = layout.CurrentStyleSheet,
            viewports = objects.OfType<Viewport>().Take(50).Select(v => new { handle = v.Handle.ToString(),
                paper_center = new[] { v.CenterPoint.X, v.CenterPoint.Y }, paper_width = v.Width, paper_height = v.Height,
                model_target = new[] { v.ViewTarget.X, v.ViewTarget.Y, v.ViewTarget.Z }, model_view_height = v.ViewHeight,
                scale = v.CustomScale, locked = v.Locked }).ToArray(),
            inserted_blocks = objects.OfType<BlockReference>().Take(50).Select(b =>
                ((BlockTableRecord)tr.GetObject(b.BlockTableRecord, OpenMode.ForRead)).Name).ToArray(),
            sheet_contents_truncated = objects.Length > 1000 };
    }
}
