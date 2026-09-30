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
        return new { layers = layers.Take(limit).Select(l => new { name = l.Name, color_index = l.Color.ColorIndex, locked = l.IsLocked, off = l.IsOff, frozen = l.IsFrozen, dependent = l.IsDependent }),
            blocks = blocks.Take(limit).Select(b => new { name = b.Name, handle = b.Handle.ToString(), xref = b.IsFromExternalReference, dependent = b.IsDependent,
                attributes = b.Cast<ObjectId>().Select(id => tr.GetObject(id, OpenMode.ForRead)).OfType<AttributeDefinition>().Take(100).Select(a => new { tag = a.Tag, text = a.TextString, constant = a.Constant }) }),
            text_styles = styles.Take(limit).Select(s => new { name = s.Name, font = s.FileName, height = s.TextSize }),
            dimension_styles = dimensionStyles.Take(limit).Select(s => s.Name), linetypes = types.Take(limit).Select(t => t.Name),
            layouts = layouts.Take(limit).Select(l => new { name = l.LayoutName, handle = l.Handle.ToString(), model = l.ModelType,
                tab_order = l.TabOrder, selected = l.TabSelected, plot_device = l.PlotConfigurationName, paper_size = l.CanonicalMediaName }),
            truncated = layers.Length > limit || blocks.Length > limit || styles.Length > limit || dimensionStyles.Length > limit || types.Length > limit || layouts.Length > limit, limit_per_category = limit };
    }
}
