using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;
using CadMcp.Core;

namespace CadMcp.AutoCAD;

internal static class Exports
{
    public static object Publish(Document document, string outputFolder, string layoutsJson, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(outputFolder)) throw new CadFault("INVALID_PATH", "Use an absolute output folder");
        var folder = Path.GetFullPath(outputFolder);
        if (!Directory.Exists(folder)) throw new CadFault("OUTPUT_FOLDER_MISSING", folder);
        using var parsed = JsonDocument.Parse(layoutsJson);
        if (parsed.RootElement.ValueKind != JsonValueKind.Array || parsed.RootElement.GetArrayLength() is < 1 or > 100)
            throw new CadFault("INVALID_LAYOUTS", "Expected an array of 1..100 layout names");
        var names = parsed.RootElement.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).ToArray();
        if (names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new CadFault("INVALID_LAYOUTS", "Layout names must be nonempty and unique");
        using (var tr = document.Database.TransactionManager.StartOpenCloseTransaction())
        {
            var dictionary = (DBDictionary)tr.GetObject(document.Database.LayoutDictionaryId, OpenMode.ForRead);
            foreach (var name in names)
                if (!dictionary.Contains(name!)) throw new CadFault("LAYOUT_NOT_FOUND", name!);
        }
        object preflight;
        using(var tr=document.Database.TransactionManager.StartOpenCloseTransaction())preflight=DrawingQuality.Release(document.Database,tr,names!);
        if(Wire.Element(preflight).Text("state")=="failed")throw new CadFault("RELEASE_PREFLIGHT_FAILED",JsonSerializer.Serialize(preflight,Wire.Json));
        var manifestPath = Path.Combine(folder, "cad-mcp-manifest.json");
        var csvPath = Path.Combine(folder, "cad-mcp-manifest.csv");
        if (File.Exists(manifestPath) || File.Exists(csvPath)) throw new CadFault("OUTPUT_EXISTS", "Manifest already exists in the output folder");
        var paths = names.Select((name, index) => Path.Combine(folder,
            (index + 1).ToString("D3") + "-" + SafeFileName(name!) + ".pdf")).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length || paths.Any(File.Exists))
            throw new CadFault("OUTPUT_EXISTS", "A PDF already exists in the output folder");
        var produced = new List<object>();
        string? error = null;
        for (int i = 0; i < names.Length; i++)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                PlotPdf(document, paths[i], names[i], null, ct);
                if (!File.Exists(paths[i]) || new FileInfo(paths[i]).Length == 0)
                    throw new CadFault("EXPORT_NOT_FOUND", "AutoCAD did not produce a nonempty PDF");
                var pdf=PdfVerification.Check(paths[i]);
                using var file = File.OpenRead(paths[i]);
                produced.Add(new { pdf, number = i + 1, layout = names[i], path = paths[i], bytes = file.Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(file)) });
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                break;
            }
        }
        var manifest = new { preflight, completeness = new { expected = names.Length, produced = produced.Count, missing_layouts = names.Skip(produced.Count).ToArray() }, drawing = document.Name, generated_at = DateTimeOffset.UtcNow,
            status = error is null ? "completed" : "partial", requested_layouts = names,
            produced, failed_layout = error is null ? null : names[produced.Count], error };
        var json = JsonSerializer.Serialize(manifest, Wire.Json);
        File.WriteAllText(manifestPath, json, Encoding.UTF8);
        var csv = new StringBuilder("number,layout,path,bytes,sha256\r\n");
        foreach (var item in produced)
        {
            var row = JsonSerializer.SerializeToElement(item, Wire.Json);
            csv.Append(row.GetProperty("number").GetInt32()).Append(',')
                .Append(Csv(row.GetProperty("layout").GetString()!)).Append(',')
                .Append(Csv(row.GetProperty("path").GetString()!)).Append(',')
                .Append(row.GetProperty("bytes").GetInt64()).Append(',')
                .Append(row.GetProperty("sha256").GetString()).Append("\r\n");
        }
        File.WriteAllText(csvPath, csv.ToString(), Encoding.UTF8);
        return new { manifest.status, manifest_path = manifestPath, csv_path = csvPath,
            files = produced, preflight, manifest.completeness, manifest.failed_layout, manifest.error,
            verification = "strict_PDF_parse_page_count_and_manifest_completeness" };
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var value = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        return string.IsNullOrEmpty(value) ? "layout" : value;
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

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
            PdfRepair.NormalizeStructure(path);
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
