using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;
using App = Autodesk.AutoCAD.ApplicationServices.Core.Application;
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
                var paper = PlotPdf(document, paths[i], names[i], null, ct);
                if (!File.Exists(paths[i]) || new FileInfo(paths[i]).Length == 0)
                    throw new CadFault("EXPORT_NOT_FOUND", "AutoCAD did not produce a nonempty PDF");
                // The PDF is written; a wrong page count read from it fails its check, a reader failure leaves the check
                // unverified, and neither stops the set.
                object pdf;
                try { pdf = PdfVerification.Check(paths[i], 1, paper); }
                catch (CadFault fault) when (fault.Code is "PDF_PAGE_COUNT" or "PDF_INVALID_MEDIA")
                { pdf = new { state = "failed", warnings = new[] { new { code = fault.Code, detail = fault.Message } } }; }
                catch (System.Exception readError) when (readError is not OperationCanceledException)
                { pdf = new { state = "unverified", warnings = new[] { new { code = "PDF_UNVERIFIED", detail = readError.GetType().Name + ": " + readError.Message } }, error = readError.GetType().Name + ": " + readError.Message }; }
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
        // Page size and empty-page findings do not fail the set, but the release needs review; a wrong page count fails it.
        var checks = produced.Select(item => Wire.Element(item).GetProperty("pdf")).ToArray();
        var reviews = checks.Where(pdf => pdf.Text("state") != "passed")
            .Select(pdf => pdf.TryGetProperty("warnings", out var warnings) ? warnings.Clone() : pdf.Clone()).ToArray();
        string pdfReview = reviews.Length == 0 ? "passed" : checks.Any(pdf => pdf.Text("state") == "failed") ? "failed" : "review_required";
        return new { manifest.status, manifest_path = manifestPath, csv_path = csvPath,
            files = produced, preflight, manifest.completeness, manifest.failed_layout, manifest.error,
            pdf_review = pdfReview, pdf_warnings = reviews.Length == 0 ? null : reviews,
            verification = "strict_PDF_parse_page_count_paper_size_nonempty_pages_and_manifest_completeness" };
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
        (double, double)? paper = null;
        if (format == "dxf") document.Database.DxfOut(path, 16, DwgVersion.Current);
        else paper = PlotPdf(document, path, layoutName, mediaName, ct);
        if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new CadFault("EXPORT_NOT_FOUND", "AutoCAD did not produce a nonempty file");
        // The file exists; an independent re-read decides whether it holds what was exported.
        object check;
        try { check = format == "dxf" ? VerifyDxf(document.Database, path) : PdfVerification.Check(path, 1, paper); }
        // A wrong page count or page size read from the file fails the check.
        catch (CadFault fault) when (fault.Code is "PDF_PAGE_COUNT" or "PDF_INVALID_MEDIA")
        { check = new { state = "failed", warnings = new[] { new { code = fault.Code, detail = fault.Message } } }; }
        // The file is written; a reader failure (unsupported fonts, compression, encryption) leaves it unverified, not failed.
        catch (System.Exception error) when (error is not OperationCanceledException)
        { check = new { state = "unverified", error = error.GetType().Name + ": " + error.Message }; }
        using var file = File.OpenRead(path);
        return new { format, path, bytes = file.Length, sha256 = Convert.ToHexString(SHA256.HashData(file)),
            layout = format == "pdf" ? layoutName ?? LayoutManager.Current.CurrentLayout : null,
            content_check = check,
            verification = format == "dxf" ? "output_hash_and_DXF_reread_entity_census" : "output_hash_strict_PDF_parse_paper_size_and_nonempty_page" };
    }

    /// <summary>
    /// Re-reads an exported DXF into a separate database and compares entity counts per layout, DXF type and
    /// layer with the source. Differences (for example proxies of vendor objects) need review.
    /// </summary>
    private static object VerifyDxf(Database source, string path)
    {
        using var copy = new Database(false, true);
        copy.DxfIn(path, null);
        var expected = Census(source); var actual = Census(copy);
        var differences = expected.Keys.Union(actual.Keys).Where(k => expected.GetValueOrDefault(k) != actual.GetValueOrDefault(k))
            .OrderBy(k => k, StringComparer.Ordinal).Take(100)
            .Select(k => new { key = k, source = expected.GetValueOrDefault(k), dxf = actual.GetValueOrDefault(k) }).ToArray();
        return new { state = differences.Length == 0 ? "passed" : "review_required", source_entities = expected.Values.Sum(), dxf_entities = actual.Values.Sum(),
            differences, key_format = "layout|DXF type|layer", method = "DxfIn into a separate database" };
    }
    private static Dictionary<string, int> Census(Database db)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (System.Collections.DictionaryEntry entry in layouts)
        {
            var layout = (Layout)tr.GetObject((ObjectId)entry.Value!, OpenMode.ForRead);
            var space = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId id in space)
            {
                if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity entity) continue;
                string key = (layout.ModelType ? "Model" : layout.LayoutName) + "|" + entity.GetRXClass().DxfName + "|" + entity.Layer;
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }
        return counts;
    }

    /// <summary>Plots one layout to PDF and returns the plotted paper size in millimetres.</summary>
    private static (double, double)? PlotPdf(Document document, string path, string? layoutName, string? mediaName, CancellationToken ct)
    {
        if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting) throw new CadFault("PLOT_BUSY", "Another AutoCAD plot is running");
        var layoutManager = LayoutManager.Current;
        var originalLayout = layoutManager.CurrentLayout;
        var originalBackgroundPlot=App.GetSystemVariable("BACKGROUNDPLOT");
        layoutName ??= originalLayout;
        try
        {
            // PlotEngine may otherwise only queue output. Receipts/hash/repair need the finished file.
            App.SetSystemVariable("BACKGROUNDPLOT",0);
            if (!string.Equals(originalLayout, layoutName, StringComparison.OrdinalIgnoreCase)) layoutManager.CurrentLayout = layoutName;
            var paper = PlotCurrentPdf(document, path, layoutName, mediaName, ct);
            PdfRepair.NormalizeStructure(path);
            return paper;
        }
        finally
        {
            try
            {
                if (!string.Equals(layoutManager.CurrentLayout, originalLayout, StringComparison.OrdinalIgnoreCase))
                    layoutManager.CurrentLayout = originalLayout;
            }
            finally { App.SetSystemVariable("BACKGROUNDPLOT",originalBackgroundPlot); }
        }
    }

    private static (double, double)? PlotCurrentPdf(Document document, string path, string layoutName, string? mediaName, CancellationToken ct)
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
        // Paper size of the validated plot, in millimetres; orientation is compared both ways.
        var size = (info.ValidatedSettings ?? settings).PlotPaperSize;
        return size.X > 0 && size.Y > 0 ? (size.X, size.Y) : null;
    }
}
