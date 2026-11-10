namespace CadMcp.Providers;

public static class CadAgent
{
    // Static instructions preserve a stable prefix; live DWG context is fetched through tools each turn.
    // The text lives in Resources/cad-agent.md, so prompt changes are reviewed as ordinary text diffs.
    private static readonly string instructions = Load();
    public static string Instructions => InstructionsFor(CadMcp.Core.UiText.Language);
    public static string InstructionsFor(string language) => instructions.Replace("Reply in the user's language.",
        language == "en"
            ? "Reply and provide progress comments in English, the installation language. English repository documentation does not change this setting. Follow an explicit user request to use another language."
            : "Reply and provide progress comments in Russian, the installation language. English repository documentation does not change this setting. Follow an explicit user request to use another language.", StringComparison.Ordinal);
    private static string Load()
    {
        using var stream = typeof(CadAgent).Assembly.GetManifestResourceStream("CadMcp.Providers.Resources.cad-agent.md")
            ?? throw new InvalidOperationException("CAD agent instructions are missing from the assembly");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n").TrimEnd();
    }
}
