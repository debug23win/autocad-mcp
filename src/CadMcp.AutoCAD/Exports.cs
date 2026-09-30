using System.IO;
using System.Security.Cryptography;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;
using CadMcp.Core;

namespace CadMcp.AutoCAD;

internal static class Exports
{
    public static object Execute(Document document, string format, string outputPath, string? layoutName, string? mediaName, CancellationToken ct)
    {
        format = format.ToLowerInvariant();
        if (format is not ("pdf" or "dxf")) throw new CadFault("INVALID_FORMAT", "Only pdf and dxf are supported");
        if (!Path.IsPathFullyQualified(outputPath)) throw new CadFault("INVALID_PATH", "Use an absolute output path");
        var path = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(path), "." + format, StringComparison.OrdinalIgnoreCase))
            throw new CadFault("INVALID_PATH", "File extension must match the requested format");
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new CadFault("OUTPUT_FOLDER_MISSING", Path.GetDirectoryName(path)!);
        if (File.Exists(path)) throw new CadFault("OUTPUT_EXISTS", "Choose a new output path; existing files are not overwritten");
        ct.ThrowIfCancellationRequested();
        if (format == "dxf") document.Database.DxfOut(path, 16, DwgVersion.Current);
        else PlotPdf(document, path, layoutName, mediaName, ct);
        if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new CadFault("EXPORT_NOT_FOUND", "AutoCAD did not produce a nonempty file");
        using var file = File.OpenRead(path);
        return new { format, path, bytes = file.Length, sha256 = Convert.ToHexString(SHA256.HashData(file)),
            layout = format == "pdf" ? layoutName ?? LayoutManager.Current.CurrentLayout : null,
            verification = "output_file_exists_and_hash_verified" };
    }

    private static void PlotPdf(Document document, string path, string? layoutName, string? mediaName, CancellationToken ct)
    {
        if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting) throw new CadFault("PLOT_BUSY", "Another AutoCAD plot is running");
        var layoutManager = LayoutManager.Current;
        var originalLayout = layoutManager.CurrentLayout;
        layoutName ??= originalLayout;
        try
        {
            if (!string.Equals(originalLayout, layoutName, StringComparison.OrdinalIgnoreCase)) layoutManager.CurrentLayout = layoutName;
            PlotCurrentPdf(document, path, layoutName, mediaName, ct);
        }
        finally
        {
            if (!string.Equals(layoutManager.CurrentLayout, originalLayout, StringComparison.OrdinalIgnoreCase))
                layoutManager.CurrentLayout = originalLayout;
        }
    }

    private static void PlotCurrentPdf(Document document, string path, string layoutName, string? mediaName, CancellationToken ct)
    {
        using var transaction = document.Database.TransactionManager.StartTransaction();
        var layouts = (DBDictionary)transaction.GetObject(document.Database.LayoutDictionaryId, OpenMode.ForRead);
        if (!layouts.Contains(layoutName)) throw new CadFault("LAYOUT_NOT_FOUND", layoutName);
        var layout = (Layout)transaction.GetObject(layouts.GetAt(layoutName), OpenMode.ForRead);
        using var info = new PlotInfo { Layout = layout.ObjectId };
        using var settings = new PlotSettings(layout.ModelType);
        settings.CopyFrom(layout);
        var validator = PlotSettingsValidator.Current;
        validator.SetPlotConfigurationName(settings, "DWG To PDF.pc3", null);
        validator.RefreshLists(settings);
        var media = validator.GetCanonicalMediaNameList(settings).Cast<string>().ToArray();
        mediaName ??= media.Contains(layout.CanonicalMediaName, StringComparer.OrdinalIgnoreCase) ? layout.CanonicalMediaName
            : media.FirstOrDefault(x => x.Contains("A3", StringComparison.OrdinalIgnoreCase))
            ?? throw new CadFault("PDF_MEDIA_MISSING", "No A3 paper size is available; supply a valid media_name");
        if (!media.Contains(mediaName, StringComparer.OrdinalIgnoreCase))
            throw new CadFault("PDF_MEDIA_INVALID", mediaName + "; available: " + string.Join(", ", media.Take(30)));
        validator.SetPlotConfigurationName(settings, "DWG To PDF.pc3", mediaName);
        validator.SetPlotType(settings, layout.ModelType ? Autodesk.AutoCAD.DatabaseServices.PlotType.Extents : Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
        if (layout.ModelType)
        {
            validator.SetUseStandardScale(settings, true);
            validator.SetStdScaleType(settings, StdScaleType.ScaleToFit);
            validator.SetPlotCentered(settings, true);
        }
        info.OverrideSettings = settings;
        using var infoValidator = new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled };
        infoValidator.Validate(info);
        ct.ThrowIfCancellationRequested();
        using var engine = PlotFactory.CreatePublishEngine();
        engine.BeginPlot(null, null);
        engine.BeginDocument(info, document.Name, null, 1, true, path);
        using (var page = new PlotPageInfo()) engine.BeginPage(page, info, true, null);
        engine.BeginGenerateGraphics(null);
        engine.EndGenerateGraphics(null);
        engine.EndPage(null);
        engine.EndDocument(null);
        engine.EndPlot(null);
    }
}
