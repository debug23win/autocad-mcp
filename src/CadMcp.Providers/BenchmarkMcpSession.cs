using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CadMcp.Core;
namespace CadMcp.Providers;

/// <summary>A dedicated real MCP connection for the benchmark, never the user's chat provider session.</summary>
public sealed class BenchmarkMcpSession : IAsyncDisposable
{
    private readonly Process process;
    private readonly ProviderLines lines;
    private readonly Task<string> errors;
    private readonly SemaphoreSlim gate = new(1, 1);
    private int nextId;
    private readonly Dictionary<string, string[]> requiredArguments = new(StringComparer.Ordinal);
    public IReadOnlyList<string> Tools { get; private set; } = [];
    public List<JsonElement> Evidence { get; } = [];
    private BenchmarkMcpSession(Process process) { this.process = process; lines = new(process.StandardOutput.BaseStream); errors = ProviderProcess.DrainErrors(process); }
    public static async Task<BenchmarkMcpSession> StartAsync(string executable, string brokerPipe, string runId, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
        info.ArgumentList.Add("--broker-pipe"); info.ArgumentList.Add(brokerPipe);
        foreach (string name in new[] { "CAD_MCP_SESSION_ID", "CAD_MCP_DOCUMENT_ID", "CAD_MCP_READ_ONLY", "CAD_MCP_PRIMARY_THREAD_FILE" }) info.Environment.Remove(name);
        info.Environment["CAD_MCP_OWNER_ID"] = "benchmark-" + runId;
        var session = new BenchmarkMcpSession(Process.Start(info) ?? throw new IOException("Cannot start benchmark MCP host"));
        try
        {
            await session.Request("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "cad_mcp_benchmark", version = Wire.Version } }, ct);
            await session.process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            var tools = await session.Request("tools/list", new { }, ct);
            var descriptors = tools.GetProperty("tools").EnumerateArray().ToArray();
            session.Tools = descriptors.Select(t => t.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToArray();
            foreach (var tool in descriptors)
                session.requiredArguments[tool.GetProperty("name").GetString()!] = tool.TryGetProperty("inputSchema", out var schema) && schema.TryGetProperty("required", out var required)
                    ? required.EnumerateArray().Select(x => x.GetString()!).ToArray() : [];
            return session;
        }
        catch { await session.DisposeAsync(); throw; }
    }
    private async Task<JsonElement> Request(string method, object parameters, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            int id = ++nextId;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }).AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            while (await lines.ReadAsync(ct) is { } json)
            {
                using (json)
                {
                    var root = json.RootElement;
                    if (ProviderJson.Id(root) != id) continue;
                    if (root.TryGetProperty("error", out var error)) throw new IOException("MCP " + method + ": " + error);
                    return root.GetProperty("result").Clone();
                }
            }
            throw new IOException("Benchmark MCP host ended: " + await errors);
        }
        finally { gate.Release(); }
    }
    public async Task<Response> CallAsync(Request request, CancellationToken ct)
    {
        if (request.Operation.StartsWith("benchmark_", StringComparison.Ordinal)) throw new InvalidOperationException("Fixture controls belong to the worker transport");
        var args = request.Data.ValueKind == JsonValueKind.Object ? request.Data.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()) : new();
        if (request.SessionId is not null) args["session_id"] = request.SessionId;
        if (request.DocumentId is not null) args["document_id"] = request.DocumentId;
        if (request.ExpectedRevision is not null) args["expected_revision"] = request.ExpectedRevision;
        if (!requiredArguments.TryGetValue(request.Operation, out var required)) throw new CadFault("BENCHMARK_TOOL_NOT_ADVERTISED", request.Operation + " is not in tools/list");
        var missing = required.Where(name => !args.TryGetValue(name, out var value) || value is null || value is JsonElement element && element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined).ToArray();
        if (missing.Length > 0) throw new CadFault("BENCHMARK_ARGUMENTS_INVALID", request.Operation + ": required MCP arguments are missing: " + string.Join(", ", missing) + "; tool was not invoked");
        var result = await Request("tools/call", new { name = request.Operation, arguments = args }, ct);
        var images = new List<object>();
        foreach (var content in result.GetProperty("content").EnumerateArray())
            if (content.Text("type") == "image")
            {
                byte[] png = Convert.FromBase64String(content.Text("data") ?? "");
                bool valid = png.Length >= 24 && png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
                var decoded = PngInspection.Read(png);
                images.Add(new { mime_type = content.Text("mimeType"), valid_png_header = valid, bytes = png.Length,
                    decoded = decoded.Decoded, non_uniform = decoded.NonUniform, decode_error = decoded.Error,
                    width = valid ? System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)) : 0,
                    height = valid ? System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)) : 0,
                    sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png)) });
            }
        var text = result.GetProperty("content").EnumerateArray().FirstOrDefault(c => c.Text("type") == "text").Text("text") ?? "{}";
        JsonElement body; try { body = JsonDocument.Parse(text).RootElement.Clone(); } catch (JsonException) { body = Wire.Element(new { text }); }
        Evidence.Add(Wire.Element(new { request_id = request.RequestId, tool = request.Operation, arguments = args, result = body, images, is_error = result.TryGetProperty("isError", out var failed) && failed.ValueKind == JsonValueKind.True }));
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("request_id", out _))
        {
            var response = JsonSerializer.Deserialize<Response>(body.GetRawText(), Wire.Json) ?? throw new IOException("Invalid MCP response");
            if (request.Operation == "cad_sessions") response = response with { SessionId = request.SessionId, DocumentId = request.DocumentId };
            if (response.Data is JsonElement { ValueKind: JsonValueKind.Object } data && images.Count > 0)
            { var fields = data.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()); fields["mcp_images"] = images; response = response with { Data = Wire.Element(fields) }; }
            return response with { RequestId = request.RequestId };
        }
        if (result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
            return Response.Fail(request, body.Text("code") ?? "MCP_TOOL_FAILED", body.Text("message") ?? text);
        if (body.ValueKind == JsonValueKind.Object && images.Count > 0)
        { var fields = body.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()); fields["mcp_images"] = images; body = Wire.Element(fields); }
        return new(request.RequestId, "completed", body, request.SessionId, request.DocumentId, request.ExpectedRevision);
    }
    public async ValueTask DisposeAsync() { await ProviderProcess.FinishAsync(process, errors); process.Dispose(); gate.Dispose(); }
}
