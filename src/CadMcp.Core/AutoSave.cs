using System.Text.Json;

namespace CadMcp.Core;

/// <summary>What the worker does with changed drawings while AutoCAD is idle.</summary>
public enum AutoSaveMode
{
    /// <summary>Nothing; only AutoCAD's own SAVETIME autosave remains.</summary>
    Off,
    /// <summary>A recovery copy in the CAD MCP data folder; the user's DWG is not touched (default).</summary>
    Copy,
    /// <summary>The active titled drawing is saved to its own file, as QSAVE does; other drawings get a recovery copy.</summary>
    Save
}

public sealed record AutoSaveSettings(AutoSaveMode Mode, int Minutes)
{
    public const int DefaultMinutes = 10, MinMinutes = 1, MaxMinutes = 240;
    public static AutoSaveSettings Default { get; } = new(AutoSaveMode.Copy, DefaultMinutes);
    public TimeSpan Interval => TimeSpan.FromMinutes(Minutes);

    public static string Name(AutoSaveMode mode) => mode switch { AutoSaveMode.Off => "off", AutoSaveMode.Save => "save", _ => "copy" };
    public static string Label(AutoSaveMode mode) => mode switch
    {
        AutoSaveMode.Off => "выключено",
        AutoSaveMode.Save => "сохранять DWG",
        _ => "резервная копия"
    };

    /// <summary>"off", "copy" or "save", optionally followed by ":minutes" (CAD_MCP_AUTOSAVE).</summary>
    public static AutoSaveSettings Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Default;
        var parts = text.Trim().Split(':', 2);
        var mode = parts[0].Trim().ToLowerInvariant() switch
        {
            "off" => AutoSaveMode.Off,
            "copy" => AutoSaveMode.Copy,
            "save" => AutoSaveMode.Save,
            _ => throw new CadFault("INVALID_AUTOSAVE", "Autosave mode is off, copy or save")
        };
        int minutes = DefaultMinutes;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out minutes) || minutes is < MinMinutes or > MaxMinutes))
            throw new CadFault("INVALID_AUTOSAVE", "Autosave interval is " + MinMinutes + ".." + MaxMinutes + " minutes");
        return new(mode, minutes);
    }

    public static AutoSaveSettings Stored(string? path = null)
    {
        try
        {
            var file = path ?? CadSettings.DefaultPath;
            if (!File.Exists(file)) return Default;
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            var mode = Parse(root.Text("autosave_mode")).Mode;
            int minutes = root.TryGetProperty("autosave_minutes", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var stored)
                ? Math.Clamp(stored, MinMinutes, MaxMinutes) : DefaultMinutes;
            return new(mode, minutes);
        }
        // A damaged or hand-edited file falls back to the safe default, a recovery copy.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CadFault or InvalidOperationException) { return Default; }
    }

    public static void Store(AutoSaveSettings settings, string? path = null)
    {
        if (settings.Minutes is < MinMinutes or > MaxMinutes) throw new ArgumentOutOfRangeException(nameof(settings));
        CadSettings.Store(json => { json["autosave_mode"] = Name(settings.Mode); json["autosave_minutes"] = settings.Minutes; }, path);
    }

    /// <summary>CAD_MCP_AUTOSAVE overrides the stored choice for unattended runs; an invalid value turns autosave off.</summary>
    public static AutoSaveSettings Effective(string? path = null)
    {
        if (Environment.GetEnvironmentVariable("CAD_MCP_AUTOSAVE") is { Length: > 0 } environment)
            try { return Parse(environment); } catch (CadFault) { return new(AutoSaveMode.Off, DefaultMinutes); }
        return Stored(path);
    }

    /// <summary>
    /// A drawing is saved or copied when it changed since its last autosave and the interval has passed since the
    /// last attempt, successful or not, so a failing drawing is retried once per interval rather than on every Idle.
    /// </summary>
    public static bool Due(long revision, long savedRevision, DateTimeOffset lastAttempt, DateTimeOffset now, TimeSpan interval) =>
        revision != savedRevision && now - lastAttempt >= interval;
}

/// <summary>Rotating recovery copies of a drawing, one folder per project.</summary>
public static class RecoveryCopies
{
    public const int Keep = 3;

    public static string Folder(string root, string projectKey) => Path.Combine(root, "autosave", projectKey);

    public static string FileName(string drawingName, DateTimeOffset now)
    {
        // Drawing names are Windows paths; take the last part whichever separator it uses.
        string name = Path.GetFileNameWithoutExtension(drawingName[(drawingName.LastIndexOfAny(['\\', '/']) + 1)..]);
        if (string.IsNullOrWhiteSpace(name)) name = "drawing";
        // Windows file name rules on every platform, so a copy made anywhere can be opened on Windows.
        name = new string(name.Select(c => c < ' ' || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        if (name.Length == 0) name = "drawing";
        if (name.Length > 80) name = name[..80];
        return now.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff") + "-" + name + ".dwg";
    }

    /// <summary>Deletes all but the newest copies (by name, which starts with the UTC time) and their records.</summary>
    public static IReadOnlyList<string> Prune(string folder, int keep = Keep)
    {
        var removed = new List<string>();
        if (!Directory.Exists(folder)) return removed;
        foreach (var old in Directory.EnumerateFiles(folder, "*.dwg").OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal).Skip(keep))
            try { File.Delete(old); File.Delete(old + ".json"); removed.Add(old); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return removed;
    }
}
