using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using CadMcp.Host;
namespace CadMcp.Tests;
public sealed class ReferenceDownloadTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request)); }
    [Fact]
    public async Task Download_follows_redirects_and_returns_exact_file_hash_in_a_unique_cache()
    {
        using var root = new TempFolder(); byte[] bytes = "%PDF-reference"u8.ToArray(); int requests = 0;
        using var client = new HttpClient(new Handler(r =>
        {
            requests++; Assert.Equal(HttpMethod.Get, r.Method);
            if (r.RequestUri!.AbsolutePath == "/start") return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("/sheet", UriKind.Relative) } };
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentType = new("application/pdf"); return response;
        }));
        var file = await ReferenceDownloads.Download("https://example.org/start", default, client, root.Path);
        Assert.Equal(2, requests); Assert.Equal(bytes, File.ReadAllBytes(file.Path)); Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), file.Sha256);
        Assert.Equal("cad_pdf_read", file.ReadWith); Assert.EndsWith(".pdf", file.Path); Assert.StartsWith(root.Path, file.Path);
        var other = await ReferenceDownloads.Download("https://example.org/start", default, client, root.Path); Assert.NotEqual(file.Path, other.Path);
    }
    [Fact]
    public async Task Unsafe_redirects_and_oversized_materials_leave_no_download_file()
    {
        using var root = new TempFolder();
        using var redirect = new HttpClient(new Handler(_ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("file:///private/file.pdf") } }));
        await Assert.ThrowsAsync<CadMcp.Core.CadFault>(() => ReferenceDownloads.Download("https://example.org/a", default, redirect, root.Path));
        using var large = new HttpClient(new Handler(_ => { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) }; response.Content.Headers.ContentLength = ReferenceDownloads.MaximumBytes + 1; return response; }));
        await Assert.ThrowsAsync<CadMcp.Core.CadFault>(() => ReferenceDownloads.Download("https://example.org/a", default, large, root.Path));
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories));
    }
    [Theory]
    [InlineData("file:///drawing.dwg")]
    [InlineData("https://user:password@example.org/a")]
    [InlineData("ftp://example.org/a")]
    public void Download_accepts_only_web_material_urls(string url) => Assert.Throws<CadMcp.Core.CadFault>(() => ReferenceDownloads.Address(url));
}
