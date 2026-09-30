using System.Text.Json;

namespace CadMcp.Providers;

public sealed record ChatState(int Provider, string CodexExecutable, string ClaudeExecutable, string Host, string Directory,
    string? SessionId, string Transcript, string? AdapterKey = null,
    string? CodexModel = null, string? CodexReasoningEffort = null);

// Atomic-save approach adapted from debug23win/ClaudeRevit HistoryStore.cs (MIT).
// Copyright (c) 2026 Alexandre Roubaud. See licenses/ClaudeRevit-MIT.txt.
// Changes: official CLI session ids instead of API history, per-process files, bounded transcript, no credentials.
public sealed class ChatStateStore(string root)
{
    private readonly string path = Path.Combine(root, "chat-" + Environment.ProcessId + ".json");
    public ChatState? Load()
    {
        if (!System.IO.Directory.Exists(root)) return null;
        foreach (var file in System.IO.Directory.EnumerateFiles(root, "chat-*.json").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                if (new FileInfo(file).Length > 4 * 1024 * 1024) continue;
                var state = JsonSerializer.Deserialize<ChatState>(File.ReadAllText(file));
                if (state is not null && Valid(state)) return state;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return null;
    }
    public static bool Valid(ChatState state) => state.Provider is 0 or 1 && state.CodexExecutable is not null && state.ClaudeExecutable is not null
        && state.Host is not null && state.Directory is not null && state.Transcript is not null;
    public static ChatState UseBundledCodex(ChatState state, string bundled, string desktop)
    {
        if (!string.Equals(state.CodexExecutable, desktop, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(state.CodexExecutable, "codex.exe", StringComparison.OrdinalIgnoreCase)) return state;
        string previousKey = "0|" + state.CodexExecutable + "|" + state.Host + "|" + state.Directory;
        return state with { CodexExecutable = bundled, AdapterKey = state.AdapterKey == previousKey
            ? "0|" + bundled + "|" + state.Host + "|" + state.Directory : state.AdapterKey };
    }
    public void Save(ChatState state)
    {
        System.IO.Directory.CreateDirectory(root);
        if (state.Transcript.Length > 500000) state = state with { Transcript = "[Earlier transcript omitted; CLI session retains its own history]\n" + state.Transcript[^500000..] };
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
