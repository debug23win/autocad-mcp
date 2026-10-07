using System.Text;
using System.Text.Json;

namespace CadMcp.Providers;

/// <summary>
/// Reads the newline-delimited JSON a provider CLI writes to stdout. One line is never allowed to grow
/// without bound, and a line that is not JSON (a warning or banner) is skipped instead of ending the turn.
/// </summary>
internal sealed class ProviderLines(TextReader reader, int maximumChars = ProviderLines.DefaultMaximumChars)
{
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
            if (start == end)
            {
                start = 0;
                end = await reader.ReadAsync(chunk.AsMemory(), ct);
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
