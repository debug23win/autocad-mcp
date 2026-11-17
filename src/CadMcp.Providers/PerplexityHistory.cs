using System.Text.Json;
using CadMcp.Core;

namespace CadMcp.Providers;

/// <summary>Local replay context, including tool receipts. Complete old turns are evicted together.</summary>
internal sealed class PerplexityHistory(string root)
{
    internal const int MaximumBytes = 64 * 1024 * 1024;
    private const int TargetBytes = 32 * 1024 * 1024;
    internal string Id { get; private set; } = "pplx-" + Guid.NewGuid().ToString("N");
    internal List<List<JsonElement>> Turns { get; private set; } = [];
    internal List<JsonElement> Current => Turns[^1];
    internal JsonElement[] Input => Turns.SelectMany(turn => turn).ToArray();
    internal string PathFor(string id) => Path.Combine(root, id + ".json");

    internal bool Load(string? id)
    {
        if (id is null) return true;
        if (!id.StartsWith("pplx-", StringComparison.Ordinal) || !Guid.TryParseExact(id[5..], "N", out _)) return false;
        string path = PathFor(id);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumBytes) return false;
            var turns = JsonSerializer.Deserialize<List<List<JsonElement>>>(File.ReadAllText(path));
            if (turns is null || turns.Count > 25 || turns.Any(turn => turn is null || turn.Any(item => item.ValueKind != JsonValueKind.Object))) return false;
            Id = id;
            Turns = turns;
            return true;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    internal int RecoverInterruptedCalls()
    {
        int recovered = 0;
        var answered = new HashSet<string>(Input.Where(item => ProviderJson.Text(item, "type") == "function_call_output")
            .Select(item => ProviderJson.Text(item, "call_id")).OfType<string>(), StringComparer.Ordinal);
        foreach (var turn in Turns)
        {
            foreach (var call in turn.Where(item => ProviderJson.Text(item, "type") == "function_call").ToArray())
            {
                string? id = ProviderJson.Text(call, "call_id");
                if (id is null || !answered.Add(id)) continue;
                turn.Insert(turn.IndexOf(call) + 1, Wire.Element(new
                {
                    type = "function_call_output", call_id = id, name = ProviderJson.Text(call, "name"),
                    output = JsonSerializer.Serialize(new { error = "CAD_CALL_OUTCOME_UNKNOWN", message = "The previous turn ended before a durable result. This call MUST NOT be replayed. Read the operation receipt and actual drawing state before continuing." })
                }));
                recovered++;
            }
        }
        return recovered;
    }

    internal int StartTurn(JsonElement input)
    {
        Turns.Add([input]);
        int removed = 0;
        while (Turns.Count > 1 && (Turns.Count > 24 || JsonSerializer.SerializeToUtf8Bytes(Turns).Length > TargetBytes))
        { Turns.RemoveAt(0); removed++; }
        return removed;
    }

    internal void Save()
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(Turns);
        if (bytes.Length > MaximumBytes) throw new IOException(UiText.T("История Perplexity достигла предельного размера. Начните новый диалог; ранее принятые CAD-операции не повторяются."));
        Directory.CreateDirectory(root);
        string path = PathFor(Id), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
