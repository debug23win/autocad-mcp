using System.Diagnostics;
using System.Text.Json;

namespace CadMcp.Providers;

public sealed record ChatState(int Provider, string CodexExecutable, string ClaudeExecutable, string Host, string Directory,
    string? SessionId, string Transcript, string? AdapterKey = null,
    string? CodexModel = null, string? CodexReasoningEffort = null,
    IReadOnlyList<ChatLine>? Messages = null, int MaxSubagents = 3,
    string? ClaudeModel = null, string? ClaudeReasoningEffort = null);

// Atomic-save approach adapted from debug23win/ClaudeRevit HistoryStore.cs (MIT).
// Copyright (c) 2026 Alexandre Roubaud. See licenses/ClaudeRevit-MIT.txt.
// Changes: official CLI session ids instead of API history, per-process files, bounded transcript, no credentials.
public sealed class ChatStateStore(string root)
{
    private const long MaximumBytes = 3 * 1024 * 1024;
    // Files of finished processes kept as fallbacks in case the newest file is damaged.
    private const int KeptFallbacks = 2;
    private readonly string path = Path.Combine(root, "chat-" + Environment.ProcessId + ".json");
    private bool pruned;
    public ChatState? Load()
    {
        if (!System.IO.Directory.Exists(root)) return null;
        // Own file first, then files of finished processes, newest first. A file of another running
        // process belongs to a chat that is still open: its messages are shown, but its CLI session
        // is not resumed, because two processes writing to one session conflict.
        var candidates = System.IO.Directory.EnumerateFiles(root, "chat-*.json")
            .Select(file => (File: file, Owner: Owner(file), Written: File.GetLastWriteTimeUtc(file)))
            .Select(f => (f.File, f.Written, Own: f.Owner == Environment.ProcessId, Foreign: f.Owner is int pid && pid != Environment.ProcessId && Running(pid)))
            .OrderByDescending(f => f.Own).ThenBy(f => f.Foreign).ThenByDescending(f => f.Written);
        foreach (var candidate in candidates)
        {
            try
            {
                if (new FileInfo(candidate.File).Length > 4 * 1024 * 1024) continue;
                var state = JsonSerializer.Deserialize<ChatState>(File.ReadAllText(candidate.File));
                if (state is not null && Valid(state)) return candidate.Foreign ? state with { SessionId = null } : state;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return null;
    }
    private static int? Owner(string file) =>
        int.TryParse(Path.GetFileNameWithoutExtension(file)["chat-".Length..], out int pid) ? pid : null;
    private static bool Running(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
    }
    public static bool Valid(ChatState state) => state.Provider is 0 or 1 && state.CodexExecutable is not null && state.ClaudeExecutable is not null
        && state.Host is not null && state.Directory is not null && state.Transcript is not null
        && state.MaxSubagents is >= 0 and <= 4;
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
        if (state.Messages is { } messages)
        {
            var recent = messages.TakeLast(200).Select(line => line with {
                Text = line.Text.Length > 100000 ? line.Text[^100000..] : line.Text,
                ReasoningSummary = line.ReasoningSummary is { Length: > 20000 } summary ? summary[^20000..] : line.ReasoningSummary,
                Steps = null
            }).ToList();
            // Measure every message once and drop the oldest until the whole state fits.
            long total = JsonSerializer.SerializeToUtf8Bytes(state with { Messages = Array.Empty<ChatLine>() }).Length;
            var sizes = recent.Select(line => JsonSerializer.SerializeToUtf8Bytes(line).Length + 1L).ToArray();
            total += sizes.Sum();
            int dropped = 0;
            while (recent.Count - dropped > 1 && total > MaximumBytes) total -= sizes[dropped++];
            state = state with { Messages = recent.Skip(dropped).ToList() };
        }
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        if (!pruned) { pruned = true; Prune(); }
    }
    /// <summary>Each CAD process writes its own file; remove the files of finished processes beyond a few fallbacks.</summary>
    private void Prune()
    {
        var stale = System.IO.Directory.EnumerateFiles(root, "chat-*.json")
            .Where(file => Owner(file) is int pid && pid != Environment.ProcessId && !Running(pid))
            .OrderByDescending(File.GetLastWriteTimeUtc).Skip(KeptFallbacks).ToArray();
        foreach (var file in stale)
            try { File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
