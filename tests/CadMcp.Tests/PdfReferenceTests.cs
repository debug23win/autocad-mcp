using System.Security.Cryptography;
using System.Text.Json;
using CadMcp.Host;
using CadMcp.Providers;
using ModelContextProtocol.Protocol;
using SkiaSharp;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace CadMcp.Tests;

public sealed class PdfReferenceTests
{
    private static JsonElement Data(CallToolResult result) => JsonDocument.Parse(((TextContentBlock)result.Content[0]).Text).RootElement.Clone();
    private static string Fixture(string name) => Path.Combine(TestEnvironment.RepositoryRoot, "tests", "CadMcp.Tests", "Fixtures", "pdf", name);

    [Fact]
    public void Pdf_text_pages_and_real_pixels_are_read_without_changing_the_source()
    {
        using var folder = new TempFolder();
        var builder = new PdfDocumentBuilder(); var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.A4).AddText("First reference 123", 15, new PdfPoint(30, 50), font);
        builder.AddPage(PageSize.A4).AddText("Second sheet 456", 15, new PdfPoint(30, 50), font);
        string path = folder.File("Reference sheet.pdf"); File.WriteAllBytes(path, builder.Build());
        var before = SHA256.HashData(File.ReadAllBytes(path));
        var result = CadTools.PdfRead(path, max_chars: 5);
        Assert.NotEqual(true, result.IsError);
        var data = Data(result); Assert.Equal(2, data.GetProperty("page_count").GetInt32());
        Assert.Equal(5, data.GetProperty("text").GetString()!.Length);
        Assert.Equal(5, data.GetProperty("next_text_offset").GetInt32());
        Assert.Equal(2, data.GetProperty("next_page").GetInt32());
        var image = Assert.IsType<ImageContentBlock>(result.Content[1]);
        using var bitmap = SKBitmap.Decode(image.DecodedData.ToArray());
        Assert.InRange(bitmap.Width, 256, 1600); Assert.InRange(bitmap.Height, 256, 1600);
        Assert.Contains("Second sheet", Data(CadTools.PdfRead(path, page: 2, render: false)).GetProperty("text").GetString());
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        File.Delete(data.GetProperty("local_image_path").GetString()!);
    }

    [Theory]
    [InlineData("copy-protected.pdf", false)]
    [InlineData("scan-copy-protected.pdf", true)]
    public void Copy_restrictions_and_scans_still_produce_readable_page_images(string name, bool scan)
    {
        var result = CadTools.PdfRead(Fixture(name)); var data = Data(result);
        Assert.NotEqual(true, result.IsError);
        Assert.Equal(scan, data.GetProperty("requires_visual_reading").GetBoolean());
        var image = Assert.IsType<ImageContentBlock>(result.Content[1]);
        using var bitmap = SKBitmap.Decode(image.DecodedData.ToArray());
        int dark = 0; for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++) if (bitmap.GetPixel(x, y).Red < 128) dark++;
        Assert.True(dark > 100, "The actual protected page must contain visible drawing pixels");
        File.Delete(data.GetProperty("local_image_path").GetString()!);
    }

    [Fact]
    public void Opening_encryption_requires_password_and_never_claims_images_bypass_it()
    {
        var blocked = CadTools.PdfRead(Fixture("opening-protected.pdf"));
        Assert.True(blocked.IsError); Assert.Equal("PDF_PASSWORD_REQUIRED", Data(blocked).GetProperty("code").GetString());
        var opened = CadTools.PdfRead(Fixture("opening-protected.pdf"), password: "test-open-password");
        Assert.NotEqual(true, opened.IsError); Assert.Contains("Restricted drawing", Data(opened).GetProperty("text").GetString());
        Assert.IsType<ImageContentBlock>(opened.Content[1]);
        File.Delete(Data(opened).GetProperty("local_image_path").GetString()!);
    }

    [Fact]
    public void Invalid_pdf_page_and_paths_return_actionable_errors()
    {
        Assert.True(CadTools.PdfRead(Fixture("copy-protected.pdf"), page: 20).IsError);
        Assert.True(CadTools.PdfRead("relative.pdf").IsError);
        using var folder = new TempFolder(); File.WriteAllText(folder.File("bad.pdf"), "not a PDF");
        Assert.True(CadTools.PdfRead(folder.File("bad.pdf")).IsError);
    }

    [Fact]
    public void Attachment_prompt_routes_pdf_and_drawing_to_readers_for_both_providers()
    {
        var files = new[] { new ChatAttachment("/reference.pdf", "reference.pdf", 100, AttachmentKind.Pdf), new ChatAttachment("/reference.dxf", "reference.dxf", 100, AttachmentKind.Drawing) };
        string prompt = ChatAttachments.AddToPrompt("Read", files);
        Assert.Contains("cad_pdf_read", prompt); Assert.Contains("cad_file_inspect", prompt);
        Assert.Contains("never open/import/edit/save", prompt);
        Assert.Contains("cad_pdf_read", JsonSerializer.Serialize(LiveInput.CodexContent(new("Read", files))));
        Assert.Contains("cad_file_inspect", JsonSerializer.Serialize(LiveInput.ClaudeMessage(new("Read", files), "test")));
    }
}
