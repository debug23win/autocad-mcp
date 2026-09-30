using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Core;

namespace CadMcp.AutoCAD;

internal static class Sheets
{
    public static Layout Layout(Database db, Transaction tr, string name, OpenMode mode = OpenMode.ForRead)
    {
        var dictionary = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        if (!dictionary.Contains(name)) throw new CadFault("LAYOUT_NOT_FOUND", name);
        var layout = (Layout)tr.GetObject(dictionary.GetAt(name), mode);
        if (layout.ModelType) throw new CadFault("PAPER_LAYOUT_REQUIRED", "Choose a paper-space layout");
        return layout;
    }

    public static BlockTableRecord Space(Database db, Transaction tr, string name) =>
        (BlockTableRecord)tr.GetObject(Layout(db, tr, name).BlockTableRecordId, OpenMode.ForWrite);

    public static object Copy(Database db, Transaction tr, JsonElement op)
    {
        string source = EditPlan.RequiredText(op, "source"), name = EditPlan.RequiredText(op, "name");
        Layout(db, tr, source);
        SymbolUtilityServices.ValidateSymbolName(name, false);
        var dictionary = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        if (dictionary.Contains(name)) throw new CadFault("LAYOUT_EXISTS", name);
        LayoutManager.Current.CopyLayout(source, name);
        var copied = Layout(db, tr, name);
        return new { op = "layout_copy", source, name, handle = copied.Handle.ToString(),
            space_handle = copied.BlockTableRecordId.Handle.ToString() };
    }

    public static object Configure(Database db, Transaction tr, JsonElement op)
    {
        var name = EditPlan.RequiredText(op, "name");
        var layout = Layout(db, tr, name, OpenMode.ForWrite);
        var validator = PlotSettingsValidator.Current;
        string device = op.Text("device") ?? "DWG To PDF.pc3";
        using var proposed = new PlotSettings(false);
        proposed.CopyFrom(layout);
        validator.SetPlotConfigurationName(proposed, device, null);
        validator.RefreshLists(proposed);
        var available = validator.GetCanonicalMediaNameList(proposed).Cast<string>().ToArray();
        string media = op.Text("media_name") ?? layout.CanonicalMediaName;
        if (!available.Contains(media, StringComparer.OrdinalIgnoreCase))
            throw new CadFault("MEDIA_NOT_FOUND", media + "; available: " + string.Join(", ", available.Take(40)));
        validator.SetPlotConfigurationName(layout, device, media);
        validator.RefreshLists(layout);
        validator.SetPlotType(layout, PlotType.Layout);
        validator.SetUseStandardScale(layout, true);
        validator.SetStdScaleType(layout, StdScaleType.StdScale1To1);
        if (op.Text("plot_style") is { } style) validator.SetCurrentStyleSheet(layout, style);
        if (op.Text("paper_units") is { } units)
            validator.SetPlotPaperUnits(layout, units switch
            {
                "millimeters" => PlotPaperUnit.Millimeters,
                "inches" => PlotPaperUnit.Inches,
                _ => throw new CadFault("INVALID_PAPER_UNITS", "Use millimeters or inches")
            });
        int rotation = op.TryGetProperty("paper_rotation", out var angle) ? angle.GetInt32() : 0;
        validator.SetPlotRotation(layout, rotation switch
        {
            0 => PlotRotation.Degrees000, 90 => PlotRotation.Degrees090,
            180 => PlotRotation.Degrees180, 270 => PlotRotation.Degrees270,
            _ => throw new CadFault("INVALID_ROTATION", "Use 0, 90, 180 or 270")
        });
        return new { op = "layout_configure", name, device = layout.PlotConfigurationName,
            media_name = layout.CanonicalMediaName, plot_style = layout.CurrentStyleSheet,
            paper_units = layout.PlotPaperUnits.ToString(), paper_rotation = layout.PlotRotation.ToString(), plot_type = layout.PlotType.ToString() };
    }

    public static void ConfigureViewport(Viewport viewport, JsonElement op)
    {
        var center = EditPlan.Point(op.GetProperty("center"));
        var target = EditPlan.Point(op.GetProperty("model_center"));
        double width = EditPlan.Numeric(op, "width"), height = EditPlan.Numeric(op, "height"), modelHeight = EditPlan.Numeric(op, "model_height");
        if (width <= 0 || height <= 0 || modelHeight <= 0 || center[2] != 0)
            throw new CadFault("INVALID_VIEWPORT", "Paper width/height and model_height must be positive; paper center Z must be zero");
        viewport.CenterPoint = new Point3d(center[0], center[1], 0);
        viewport.Width = width;
        viewport.Height = height;
        viewport.ViewDirection = Vector3d.ZAxis;
        viewport.ViewTarget = new Point3d(target[0], target[1], target[2]);
        viewport.ViewCenter = Point2d.Origin;
        viewport.ViewHeight = modelHeight;
        viewport.CustomScale = height / modelHeight;
        viewport.TwistAngle = EditPlan.Numeric(op, "twist_deg", 0) * Math.PI / 180;
        viewport.Locked = op.TryGetProperty("locked", out var locked) ? locked.GetBoolean() : true;
    }

    public static void TurnOnViewport(string layoutName, Viewport viewport)
    {
        var manager = LayoutManager.Current;
        var previous = manager.CurrentLayout;
        try
        {
            if (!string.Equals(previous, layoutName, StringComparison.OrdinalIgnoreCase)) manager.CurrentLayout = layoutName;
            viewport.On = true;
        }
        finally
        {
            if (!string.Equals(manager.CurrentLayout, previous, StringComparison.OrdinalIgnoreCase)) manager.CurrentLayout = previous;
        }
    }

    public static void InitializeLayoutView(string layoutName)
    {
        var manager = LayoutManager.Current;
        var previous = manager.CurrentLayout;
        try
        {
            // The first activation creates AutoCAD's reserved paper-space viewport.
            // Create it before appending the user's model viewport so AutoCAD does not resize that viewport as the page view.
            if (!string.Equals(previous, layoutName, StringComparison.OrdinalIgnoreCase)) manager.CurrentLayout = layoutName;
        }
        finally
        {
            if (!string.Equals(manager.CurrentLayout, previous, StringComparison.OrdinalIgnoreCase)) manager.CurrentLayout = previous;
        }
    }
}
