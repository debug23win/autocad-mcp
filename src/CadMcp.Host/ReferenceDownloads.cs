using System.Net;
using System.Security.Cryptography;
using CadMcp.Core;

namespace CadMcp.Host;

internal sealed record DownloadedReference(string Url, string FinalUrl, string Path, long Bytes, string Sha256, string? ReadWith);

internal static class ReferenceDownloads
{
    internal const long MaximumBytes = 100L * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false }) { Timeout = TimeSpan.FromSeconds(90) };
    internal static Uri Address(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            throw new CadFault("INVALID_URL", "Use an absolute HTTP/HTTPS material URL without embedded credentials");
        return uri;
    }
    internal static async Task<DownloadedReference> Download(string url, CancellationToken ct, HttpClient? client = null, string? root = null, TimeSpan? maximumDuration = null, TimeSpan? bodySilence = null)
    {
        var caller = ct;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(maximumDuration ?? TimeSpan.FromSeconds(90));
        ct = deadline.Token;
        try
        {
            var uri = Address(url); client ??= Client;
            for (int redirect = 0; redirect <= 5; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("CAD-MCP/" + Wire.Version);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (redirect == 5 || response.Headers.Location is not { } location) throw new CadFault("DOWNLOAD_REDIRECT", "Too many redirects or missing redirect location");
                    var next = Address(new Uri(uri, location).AbsoluteUri);
                    if (uri.Scheme == "https" && next.Scheme != "https") throw new CadFault("DOWNLOAD_REDIRECT", "HTTPS to HTTP downgrade refused");
                    uri = next; continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaximumBytes) throw new CadFault("FILE_TOO_LARGE", "Downloads are limited to 100 MiB");
                string name = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last());
                name = name.Replace('\\', '/').Split('/').Last();
                name = new string(name.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
                if (name.Length > 120) name = name[^120..];
                if (string.IsNullOrWhiteSpace(name)) name = "reference";
                if (!Path.HasExtension(name)) name += response.Content.Headers.ContentType?.MediaType switch
                { "application/pdf" => ".pdf", "image/png" => ".png", "image/jpeg" => ".jpg", "text/html" => ".html", _ => ".bin" };
                // Content is only stored as reference data. Downloaded executables/scripts are never run.
                string folder = System.IO.Path.Combine(root ?? Wire.DataDirectory("downloads"), Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                string path = System.IO.Path.Combine(folder, name);
                try
                {
                    long bytes = 0;
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    await using (var input = await response.Content.ReadAsStreamAsync(ct))
                    await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        byte[] buffer = new byte[81920]; int read;
                        while (true)
                        {
                            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            idle.CancelAfter(bodySilence ?? TimeSpan.FromSeconds(30));
                            read = await input.ReadAsync(buffer, idle.Token);
                            if (read == 0) break;
                            bytes += read;
                            if (bytes > MaximumBytes) throw new CadFault("FILE_TOO_LARGE", "Downloads are limited to 100 MiB");
                            hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), ct);
                        }
                    }
                    string? reader = Path.GetExtension(path).ToLowerInvariant() switch { ".pdf" => "cad_pdf_read", ".dwg" or ".dxf" => "cad_file_inspect", _ => null };
                    return new(url, uri.AbsoluteUri, path, bytes, Convert.ToHexString(hash.GetHashAndReset()), reader);
                }
                catch { if (File.Exists(path)) File.Delete(path); if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); throw; }
            }
            throw new CadFault("DOWNLOAD_REDIRECT", "Redirect limit exceeded");
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        { throw new CadFault("DOWNLOAD_TIMEOUT", "Download deadline or body inactivity limit expired; partial output was removed."); }
    }
}
