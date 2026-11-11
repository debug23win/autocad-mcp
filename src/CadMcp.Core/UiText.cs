using System.Globalization;
using System.Text.Json;

namespace CadMcp.Core;

/// <summary>Installation-language text. Never changes AutoCAD's thread culture or drawing data.</summary>
public static class UiText
{
    private static readonly Lazy<string> language = new(() => ReadLanguage(Path.Combine(Wire.DataRoot, "installation-language.txt")));
    private static readonly Lazy<IReadOnlyDictionary<string, string>> english = new(LoadEnglish);
    public static string Language => language.Value;
    public static string T(string russian) => Translate(russian, Language);
    public static string Format(FormattableString russian) => FormatFor(russian, Language);

    public static string Translate(string russian, string language) => Normalize(language) == "en" && english.Value.TryGetValue(russian, out var text) ? text : russian;
    public static string FormatFor(FormattableString russian, string language) =>
        string.Format(CultureInfo.CurrentCulture, Translate(russian.Format, language), russian.GetArguments());

    internal static string Normalize(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "en" or "english" or "en-us" or "en-gb" => "en",
        _ => "ru"
    };

    internal static string ReadLanguage(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length <= 64 ? Normalize(File.ReadAllText(path)) : "ru"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return "ru"; }
    }

    private static IReadOnlyDictionary<string, string> LoadEnglish()
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream("CadMcp.Core.Resources.ui.en.json")
            ?? throw new InvalidOperationException("English UI resources are missing");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException("English UI resources are invalid");
    }
}
