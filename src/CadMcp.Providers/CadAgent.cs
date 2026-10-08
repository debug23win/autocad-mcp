namespace CadMcp.Providers;

public static class CadAgent
{
    // Static instructions preserve a stable prefix; live DWG context is fetched through tools each turn.
    // The text lives in Resources/cad-agent.md, so prompt changes are reviewed as ordinary text diffs.
    public static string Instructions { get; } = Load();
    private static string Load()
    {
        using var stream = typeof(CadAgent).Assembly.GetManifestResourceStream("CadMcp.Providers.Resources.cad-agent.md")
            ?? throw new InvalidOperationException("CAD agent instructions are missing from the assembly");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n").TrimEnd();
    }
}
