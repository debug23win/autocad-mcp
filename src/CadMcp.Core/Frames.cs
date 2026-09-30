// Adapted from beiming183-cloud/AutoCAD-MCP PipeServer.cs (MIT).
// Copyright (c) 2024 AutoCAD MCP Server Contributors. See licenses/beiming-MIT.txt.
// Changes: reusable framing, reject truncated messages, bound outgoing frames, cancellation.
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace CadMcp.Core;

public static class Frames
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        int first = await stream.ReadAsync(header, 0, 1, ct);
        if (first == 0) return null;
        await ReadFully(stream, header, 1, 3, ct);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumBytes) throw new InvalidDataException("Invalid frame length");
        var body = new byte[length];
        await ReadFully(stream, body, 0, body.Length, ct);
        return body;
    }
    public static async Task WriteAsync(Stream stream, byte[] body, CancellationToken ct)
    {
        if (body.Length == 0 || body.Length > MaximumBytes) throw new InvalidDataException("Frame exceeds limit");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, 0, header.Length, ct);
        await stream.WriteAsync(body, 0, body.Length, ct);
        await stream.FlushAsync(ct);
    }
    private static async Task ReadFully(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        while (count > 0) { int n = await stream.ReadAsync(buffer, offset, count, ct); if (n == 0) throw new EndOfStreamException(); offset += n; count -= n; }
    }
}

public static class PipeClient
{
    public static async Task<Response> CallAsync(string pipeName, Request request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(30000, timeout.Token);
        request = request with { Deadline = request.Deadline ?? DateTimeOffset.UtcNow.AddSeconds(25), Data = request.Data.ValueKind == JsonValueKind.Undefined ? Wire.Element(new { }) : request.Data };
        await Frames.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(request, Wire.Json), timeout.Token);
        var data = await Frames.ReadAsync(pipe, timeout.Token) ?? throw new EndOfStreamException();
        var result = JsonSerializer.Deserialize<Response>(data, Wire.Json) ?? throw new InvalidDataException("Empty response");
        if (result.RequestId != request.RequestId) throw new InvalidDataException("Request/response mismatch");
        return result;
    }
}

public sealed class PipeServer(string name, Func<Request, CancellationToken, Task<Response>> handler) : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private Task? task;
    public Task Completion => task ?? Task.CompletedTask;
    public void Start() => task = Task.Run(ListenAsync);
    private async Task ListenAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stop.Token);
                _ = HandleAsync(pipe);
                pipe = null;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                System.Diagnostics.Trace.WriteLine(e.Message);
                try { await Task.Delay(100, stop.Token); } catch (OperationCanceledException) { break; }
            }
            finally { pipe?.Dispose(); }
        }
    }
    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            try
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var bytes = await Frames.ReadAsync(pipe, timeout.Token);
                if (bytes is null) return;
                var request = JsonSerializer.Deserialize<Request>(bytes, Wire.Json) ?? throw new JsonException();
                Response response;
                try
                {
                    if (request.Deadline is null || request.Deadline <= DateTimeOffset.UtcNow)
                        throw new CadFault("DEADLINE_EXPIRED", "Request needs a future deadline");
                    timeout.CancelAfter(request.Deadline.Value - DateTimeOffset.UtcNow);
                    response = await handler(request, timeout.Token);
                }
                catch (CadFault e) { response = Response.Fail(request, e.Code, e.Message); }
                catch (OperationCanceledException) { response = Response.Fail(request, "TIMEOUT", "Request expired; refresh context before retrying"); }
                catch (Exception e) { response = Response.Fail(request, "INTERNAL_ERROR", e.Message); }
                await Frames.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(response, Wire.Json), stop.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or JsonException)
            { System.Diagnostics.Trace.WriteLine(e.Message); }
        }
    }
    private NamedPipeServerStream CreatePipe()
    {
        return new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }
    public void Dispose() { stop.Cancel(); /* listener releases pipe asynchronously; do not block the CAD thread */ }
}
