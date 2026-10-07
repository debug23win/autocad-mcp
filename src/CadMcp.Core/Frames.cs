// Adapted from beiming183-cloud/AutoCAD-MCP PipeServer.cs (MIT).
// Copyright (c) 2024 AutoCAD MCP Server Contributors. See licenses/beiming-MIT.txt.
// Changes: reusable framing, reject truncated messages, bound outgoing frames, cancellation, draining shutdown.
using System.Buffers.Binary;
using System.Collections.Concurrent;
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
        double waitSeconds=request.Operation=="cad_render"?90:30;
        timeout.CancelAfter(TimeSpan.FromSeconds(waitSeconds));
        try
        {
            // CurrentUserOnly also checks that the server end of the pipe belongs to this user,
            // so another account cannot create the pipe first and receive CAD requests.
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(30000, timeout.Token);
            request = request with { Deadline = request.Deadline ?? DateTimeOffset.UtcNow.AddSeconds(waitSeconds-5), Data = request.Data.ValueKind == JsonValueKind.Undefined ? Wire.Element(new { }) : request.Data };
            await Frames.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(request, Wire.Json), timeout.Token);
            var data = await Frames.ReadAsync(pipe, timeout.Token) ?? throw new EndOfStreamException();
            var result = JsonSerializer.Deserialize<Response>(data, Wire.Json) ?? throw new InvalidDataException("Empty response");
            if (result.RequestId != request.RequestId) throw new InvalidDataException("Request/response mismatch");
            return result;
        }
        // Callers recover from lost responses by catching IOException; report every transport failure that way.
        catch (Exception e) when (e is TimeoutException or InvalidDataException or JsonException or UnauthorizedAccessException)
        { throw new IOException("CAD MCP pipe transport failed: " + e.Message, e); }
    }
}

public sealed class PipeServer(string name, Func<Request, CancellationToken, Task<Response>> handler) : IDisposable
{
    /// <summary>Upper bound for a caller-supplied deadline, so one request cannot hold a pipe instance indefinitely.</summary>
    public static readonly TimeSpan MaximumRequestDuration = TimeSpan.FromMinutes(5);
    // Neither source owns a timer or wait handle; they live as long as the server and need no disposal.
    private readonly CancellationTokenSource listening = new(), stop = new();
    private readonly ConcurrentDictionary<Task, byte> handlers = new();
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task listener = Task.CompletedTask;
    /// <summary>Completes after <see cref="StopAsync"/> or <see cref="Dispose"/>.</summary>
    public Task Completion => stopped.Task;
    public void Start() => listener = Task.Run(ListenAsync);
    private async Task ListenAsync()
    {
        NamedPipeServerStream? waiting = null;
        try
        {
            while (!listening.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = waiting ?? CreatePipe(); waiting = null;
                    await pipe.WaitForConnectionAsync(listening.Token);
                    // Open the next instance before handing this one over, so a client always finds a listening
                    // instance. A quick request can finish synchronously; on Unix the last instance closing
                    // would also close the shared socket and reset connections that are already queued.
                    try { waiting = CreatePipe(); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.WriteLine(e.Message); }
                    Track(HandleAsync(pipe));
                    pipe = null;
                }
                catch (OperationCanceledException) when (listening.IsCancellationRequested) { break; }
                catch (Exception e)
                {
                    System.Diagnostics.Trace.WriteLine(e.Message);
                    try { await Task.Delay(100, listening.Token); } catch (OperationCanceledException) { break; }
                }
                finally { pipe?.Dispose(); }
            }
        }
        finally { waiting?.Dispose(); }
    }
    private void Track(Task request)
    {
        handlers.TryAdd(request, 0);
        _ = request.ContinueWith(done => handlers.TryRemove(done, out _), TaskScheduler.Default);
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
                var request = JsonSerializer.Deserialize<Request>(bytes, Wire.Json) ?? throw new JsonException("Empty request");
                Response response;
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    if (request.Deadline is null || request.Deadline <= now)
                        throw new CadFault("DEADLINE_EXPIRED", "Request needs a future deadline");
                    var remaining = request.Deadline.Value - now;
                    timeout.CancelAfter(remaining < MaximumRequestDuration ? remaining : MaximumRequestDuration);
                    response = await handler(request, timeout.Token);
                }
                catch (CadFault e) { response = Response.Fail(request, e.Code, e.Message); }
                catch (OperationCanceledException) { response = Response.Fail(request, "TIMEOUT", "Request expired; refresh context before retrying"); }
                catch (Exception e) { response = Response.Fail(request, "INTERNAL_ERROR", e.Message); }
                var reply = JsonSerializer.SerializeToUtf8Bytes(response, Wire.Json);
                if (reply.Length > Frames.MaximumBytes)
                    reply = JsonSerializer.SerializeToUtf8Bytes(Response.Fail(request, "RESPONSE_TOO_LARGE", "Response exceeds the pipe frame limit; request a narrower result"), Wire.Json);
                // The caller is waiting for this reply, so write it even while the server is stopping.
                using var write = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Frames.WriteAsync(pipe, reply, write.Token);
            }
            // This task is not awaited by anyone; never let it fault unobserved.
            catch (Exception e) { System.Diagnostics.Trace.WriteLine("CAD MCP pipe request: " + e.Message); }
        }
    }
    private NamedPipeServerStream CreatePipe()
    {
        return new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }
    /// <summary>Stop accepting connections, let requests in progress finish within <paramref name="drain"/>, then cancel the rest.</summary>
    public async Task StopAsync(TimeSpan drain)
    {
        listening.Cancel();
        try { await Task.WhenAll(handlers.Keys).WaitAsync(drain); }
        catch (TimeoutException) { }
        stop.Cancel();
        try { await Task.WhenAll(handlers.Keys.Append(listener)).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { }
        stopped.TrySetResult();
    }
    public void Dispose() { listening.Cancel(); stop.Cancel(); stopped.TrySetResult(); /* listener releases pipe asynchronously; do not block the CAD thread */ }
}
