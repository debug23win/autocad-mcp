using System.Text;
using System.Text.Json;

namespace CadMcp.Providers;

/// <summary>
/// Reads the newline-delimited JSON a provider CLI writes to stdout. One line is never allowed to grow
/// without bound, and a line that is not JSON (a warning or banner) is skipped instead of ending the turn.
/// </summary>
internal sealed class ProviderLines(TextReader reader, int maximumChars = ProviderLines.DefaultMaximumChars)
{
    // StreamReader.ReadAsync may fill the requested character buffer after a complete line,
    // blocking on a live pipe whose writer is waiting for our reply. Read available bytes instead.
    internal ProviderLines(Stream pipe, int maximumChars = DefaultMaximumChars) : this(new PipeTextReader(pipe), maximumChars) { }

    private sealed class PipeTextReader(Stream pipe) : TextReader
    {
        private readonly byte[] bytes = new byte[8192];
        private readonly char[] decoded = new char[Encoding.UTF8.GetMaxCharCount(8192)];
        private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
        private int position, length;
        private bool ended, first = true;
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return 0;
            while (position == length)
            {
                if (ended) return 0;
                int count = await pipe.ReadAsync(bytes, cancellationToken);
                ended = count == 0;
                position = 0;
                length = decoder.GetChars(bytes.AsSpan(0, count), decoded, flush: ended);
                if (first && length > 0) { first = false; if (decoded[0] == '\uFEFF') position = 1; }
            }
            int copied = Math.Min(buffer.Length, length - position);
            decoded.AsMemory(position, copied).CopyTo(buffer); position += copied;
            return copied;
        }
    }
    // Tool results with base64 images are legitimately several megabytes long.
    public const int DefaultMaximumChars = 32 * 1024 * 1024;
    private const int KeptNotes = 8;
    private readonly char[] chunk = new char[16384];
    private readonly StringBuilder line = new();
    private readonly Queue<string> notes = new();
    private int start, end;

    /// <summary>The last few skipped lines, for an error message when the CLI exits unexpectedly.</summary>
    public string Skipped => string.Join(Environment.NewLine, notes);

    /// <summary>Returns the next JSON line, or null at the end of the stream.</summary>
    public async Task<JsonDocument?> ReadAsync(CancellationToken ct)
    {
        while (await ReadLineAsync(ct) is { } text)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            try { return JsonDocument.Parse(text); }
            catch (JsonException) { Note(text.Length > 300 ? text[..300] + "…" : text); }
        }
        return null;
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        line.Clear();
        bool overflow = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (start == end)
            {
                start = 0;
                end = await reader.ReadAsync(chunk.AsMemory(), ct);
                // Killing a cancelled CLI can complete its pending read as EOF instead of a cancelled task.
                ct.ThrowIfCancellationRequested();
                if (end == 0)
                {
                    if (overflow) { Note("Skipped an oversized provider line"); return null; }
                    return line.Length > 0 ? Finish() : null;
                }
            }
            int newline = Array.IndexOf(chunk, '\n', start, end - start);
            int stop = newline < 0 ? end : newline;
            if (!overflow && line.Length + (stop - start) > maximumChars) { overflow = true; line.Clear(); }
            if (!overflow) line.Append(chunk, start, stop - start);
            start = newline < 0 ? end : newline + 1;
            if (newline < 0) continue;
            if (!overflow) return Finish();
            Note("Skipped a provider line longer than " + maximumChars + " characters");
            overflow = false;
        }
    }

    private string Finish()
    {
        if (line.Length > 0 && line[^1] == '\r') line.Length--;
        return line.ToString();
    }

    private void Note(string text)
    {
        notes.Enqueue(text);
        while (notes.Count > KeptNotes) notes.Dequeue();
    }
}
