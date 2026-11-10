using System.Text.Json;
using CadMcp.Core;
namespace CadMcp.Providers;

public sealed record ChatPreferences(int Provider, string? CodexModel, string? CodexEffort, string? ClaudeModel, string? ClaudeEffort, bool ShowProgress = true);
public sealed class ChatPreferencesStore(string? path = null)
{
    private readonly string path = path ?? Wire.DataDirectory("chat", "preferences.json");
    public ChatPreferences? Load()
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16384) return null;
            var value = JsonSerializer.Deserialize<ChatPreferences>(File.ReadAllText(path), Wire.Json);
            return value is { Provider: 0 or 1 } ? value : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public void Save(ChatPreferences value)
    {
        if (value.Provider is not (0 or 1)) throw new InvalidDataException("Unknown chat provider");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, Wire.Json)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
