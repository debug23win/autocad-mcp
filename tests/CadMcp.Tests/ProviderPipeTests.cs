using System.IO.Pipes;
using System.Text;
using CadMcp.Providers;

namespace CadMcp.Tests;

public sealed class ProviderPipeTests
{
    [Theory]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(16384)]
    [InlineData(32768)]
    public async Task Complete_JSON_at_buffer_boundary_is_returned_while_the_writer_stays_open(int byteCount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var writer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var input = new AnonymousPipeClientStream(PipeDirection.In, writer.ClientSafePipeHandle);
        string value = new('x', byteCount - 12);
        byte[] payload = Encoding.UTF8.GetBytes("{\"text\":\"" + value + "\"}\n");
        Assert.Equal(byteCount, payload.Length);
        var send = writer.WriteAsync(payload, timeout.Token).AsTask();
        using var message = await new ProviderLines(input).ReadAsync(timeout.Token);
        Assert.Equal(value, message!.RootElement.GetProperty("text").GetString());
        await send;
        Assert.True(writer.CanWrite); // No EOF or following protocol event released the read.
    }

    [Fact]
    public async Task Fragmented_UTF8_BOM_surrogates_and_CRLF_survive_live_pipe_decoding()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var writer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var input = new AnonymousPipeClientStream(PipeDirection.In, writer.ClientSafePipeHandle);
        const string text = "Сеть ✓ 🚜";
        byte[] payload = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"text\":\"" + text + "\"}\r\n")).ToArray();
        async Task Send()
        {
            foreach (byte part in payload) { await writer.WriteAsync(new byte[] { part }, timeout.Token); await Task.Delay(1, timeout.Token); }
        }
        var send = Send();
        using var message = await new ProviderLines(input).ReadAsync(timeout.Token);
        Assert.Equal(text, message!.RootElement.GetProperty("text").GetString());
        await send;
    }

    [Fact]
    public async Task Pipe_decoding_preserves_bounded_line_rejection_and_recovers_the_next_message()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var writer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var input = new AnonymousPipeClientStream(PipeDirection.In, writer.ClientSafePipeHandle);
        var send = writer.WriteAsync(Encoding.UTF8.GetBytes(new string('x', 128) + "\n{\"ok\":true}\n"), timeout.Token).AsTask();
        var lines = new ProviderLines(input, maximumChars: 20);
        using var message = await lines.ReadAsync(timeout.Token);
        Assert.True(message!.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("longer than", lines.Skipped);
        await send;
    }
}
