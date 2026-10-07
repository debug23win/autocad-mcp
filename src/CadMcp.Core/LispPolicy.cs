using System.Text.Json;
using System.Text.Json.Nodes;

namespace CadMcp.Core;

/// <summary>How cad_lisp code is admitted. AutoLISP can start processes, write files and load code.</summary>
public enum LispPolicyMode
{
    /// <summary>Every script waits for the user's confirmation in AutoCAD (default).</summary>
    Ask,
    /// <summary>Scripts without risky constructs run at once; risky ones still ask.</summary>
    AutoSafe,
    /// <summary>Every script runs without confirmation (behaviour before policies existed).</summary>
    Allow,
    /// <summary>cad_lisp is rejected.</summary>
    Deny
}

public sealed record LispFinding(string Code, string Severity, string Detail);
/// <summary>What the user sees before a waiting cad_lisp script runs.</summary>
public sealed record LispApprovalRequest(string OperationId, string Drawing, string Code, IReadOnlyList<LispFinding> Findings);

/// <summary>
/// Static review of cad_lisp code before it reaches AutoCAD. The scan is a safety net for the
/// AutoSafe policy and an explanation for the confirmation dialog, not a sandbox: dynamic code
/// (eval/read) is itself reported as risky because it can hide any call.
/// </summary>
public static class LispPolicy
{
    public const string Blocked = "blocked", Risky = "risky", Notice = "notice";

    private static readonly Dictionary<string, (string Code, string Detail)> RiskyFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["startapp"] = ("LISP_EXTERNAL_PROCESS", "starts an external program"),
        ["vlax-create-object"] = ("LISP_EXTERNAL_PROCESS", "creates an external COM object (for example a shell)"),
        ["vlax-get-or-create-object"] = ("LISP_EXTERNAL_PROCESS", "creates an external COM object"),
        ["vlax-get-object"] = ("LISP_EXTERNAL_PROCESS", "connects to an external COM application"),
        ["vla-sendcommand"] = ("LISP_SEND_COMMAND", "runs command text that cannot be reviewed in advance"),
        ["open"] = ("LISP_FILE_WRITE", "opens a file for reading or writing"),
        ["write-line"] = ("LISP_FILE_WRITE", "writes to a file"),
        ["write-char"] = ("LISP_FILE_WRITE", "writes to a file"),
        ["vl-file-delete"] = ("LISP_FILE_WRITE", "deletes a file"),
        ["vl-file-rename"] = ("LISP_FILE_WRITE", "renames a file"),
        ["vl-file-copy"] = ("LISP_FILE_WRITE", "copies a file"),
        ["vl-mkdir"] = ("LISP_FILE_WRITE", "creates a folder"),
        ["load"] = ("LISP_CODE_LOADING", "loads and runs code from a file"),
        ["arxload"] = ("LISP_CODE_LOADING", "loads a native ARX module"),
        ["autoarxload"] = ("LISP_CODE_LOADING", "registers a native ARX module"),
        ["autoload"] = ("LISP_CODE_LOADING", "registers code loaded from a file"),
        ["vl-load-all"] = ("LISP_CODE_LOADING", "loads code into every drawing"),
        ["vl-vbaload"] = ("LISP_CODE_LOADING", "loads a VBA project"),
        ["vl-vbarun"] = ("LISP_CODE_LOADING", "runs a VBA macro"),
        ["vl-registry-write"] = ("LISP_REGISTRY", "writes the Windows registry"),
        ["vl-registry-delete"] = ("LISP_REGISTRY", "deletes from the Windows registry"),
        ["setenv"] = ("LISP_REGISTRY", "changes AutoCAD settings in the registry"),
        ["eval"] = ("LISP_DYNAMIC_CODE", "evaluates code built at run time"),
        ["read"] = ("LISP_DYNAMIC_CODE", "turns text into code at run time"),
        ["vl-symbol-value"] = ("LISP_DYNAMIC_CODE", "calls functions by computed name"),
        ["vl-propagate"] = ("LISP_DYNAMIC_CODE", "copies variables into every drawing"),
        ["vl-bb-set"] = ("LISP_DYNAMIC_CODE", "writes shared variables of other drawings")
    };
    private static readonly Dictionary<string, (string Code, string Detail)> RiskyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SHELL"] = ("LISP_EXTERNAL_PROCESS", "runs an operating-system command"),
        ["SH"] = ("LISP_EXTERNAL_PROCESS", "runs an operating-system command"),
        ["NETLOAD"] = ("LISP_CODE_LOADING", "loads a .NET assembly into AutoCAD"),
        ["APPLOAD"] = ("LISP_CODE_LOADING", "loads application code"),
        ["ARX"] = ("LISP_CODE_LOADING", "loads a native ARX module"),
        ["SCRIPT"] = ("LISP_CODE_LOADING", "runs a script file"),
        ["VBALOAD"] = ("LISP_CODE_LOADING", "loads a VBA project"),
        ["VBARUN"] = ("LISP_CODE_LOADING", "runs a VBA macro"),
        ["VBASTMT"] = ("LISP_CODE_LOADING", "runs a VBA statement")
    };
    // Switching or closing drawings from inside the running script breaks the operation's document
    // tracking and can crash AutoCAD; drawings are opened and activated by the user.
    private static readonly HashSet<string> DocumentCommands = new(StringComparer.OrdinalIgnoreCase)
    { "OPEN", "NEW", "QNEW", "CLOSE", "CLOSEALL", "QUIT", "EXIT", "RECOVER", "RECOVERALL", "PARTIALOPEN" };
    private static readonly HashSet<string> DocumentFunctions = new(StringComparer.OrdinalIgnoreCase) { "vla-open", "vla-close", "vla-activate" };
    // Commands that open a dialog, palette or in-place editor even with FILEDIA/CMDDIA off; a script then waits
    // for the user. Most have a command-line form with a leading hyphen.
    private static readonly HashSet<string> DialogCommands = new(StringComparer.OrdinalIgnoreCase)
    { "OPTIONS", "DIMSTYLE", "STYLE", "PAGESETUP", "BEDIT", "UNITS", "DSETTINGS", "OSNAP", "ATTEDIT", "EATTEDIT", "DDEDIT", "TEXTEDIT",
      "MTEDIT", "TABLESTYLE", "MLEADERSTYLE", "MATBROWSEROPEN" };
    private static readonly HashSet<string> CommandFunctions = new(StringComparer.OrdinalIgnoreCase) { "command", "command-s", "vl-cmdf" };

    public static LispPolicyMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "ask" => LispPolicyMode.Ask,
        "auto_safe" or "autosafe" or "safe" => LispPolicyMode.AutoSafe,
        "allow" => LispPolicyMode.Allow,
        "deny" or "off" => LispPolicyMode.Deny,
        _ => throw new CadFault("INVALID_LISP_POLICY", "Use ask, auto_safe, allow or deny")
    };
    /// <summary>Russian label shown to the user in AutoCAD and in the chat panel.</summary>
    public static string Label(LispPolicyMode mode) => mode switch
    {
        LispPolicyMode.AutoSafe => "без вопросов, если в коде нет опасных функций", LispPolicyMode.Allow => "разрешён без вопросов",
        LispPolicyMode.Deny => "запрещён", _ => "спрашивать перед каждым запуском"
    };
    public static string Name(LispPolicyMode mode) => mode switch
    { LispPolicyMode.AutoSafe => "auto_safe", LispPolicyMode.Allow => "allow", LispPolicyMode.Deny => "deny", _ => "ask" };

    /// <summary>Findings in source order, at most one per code and name.</summary>
    public static IReadOnlyList<LispFinding> Scan(string code)
    {
        var tokens = Tokenize(code);
        var findings = new List<LispFinding>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string findingCode, string severity, string detail) { if (seen.Add(findingCode + "|" + detail)) findings.Add(new(findingCode, severity, detail)); }
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind != TokenKind.Symbol) continue;
            string name = token.Text.TrimStart('\'');
            if (RiskyFunctions.TryGetValue(name, out var risky)) Add(risky.Code, Risky, name + ": " + risky.Detail);
            if (name.StartsWith("dos_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("acet-sys-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("acet-file-", StringComparison.OrdinalIgnoreCase))
                Add("LISP_EXTERNAL_PROCESS", Risky, name + ": operating-system extension function");
            if (name.StartsWith("vlr-", StringComparison.OrdinalIgnoreCase)) Add("LISP_REACTOR", Risky, name + ": installs a reactor that runs code later");
            if (DocumentFunctions.Contains(name)) Add("LISP_DOCUMENT_SWITCH", Blocked, name + ": opens, closes or activates a drawing");
            if (!CommandFunctions.Contains(name) || i == 0 || tokens[i - 1].Kind != TokenKind.Open) continue;
            var argument = i + 1 < tokens.Count ? tokens[i + 1] : default;
            if (argument.Kind != TokenKind.String) continue;
            string command = argument.Text.Trim();
            bool commandLine = command.TrimStart('_', '.', '+').StartsWith('-');
            command = command.TrimStart('_', '.', '+', '-').Split(' ', '\t')[0];
            if (command.Length == 0) continue;
            if (DocumentCommands.Contains(command)) Add("LISP_DOCUMENT_SWITCH", Blocked, command + ": opens, closes or switches drawings");
            if (RiskyCommands.TryGetValue(command, out var riskyCommand)) Add(riskyCommand.Code, Risky, command + ": " + riskyCommand.Detail);
            if (!commandLine && DialogCommands.Contains(command)) Add("LISP_DIALOG_COMMAND", Notice, command + ": may open a dialog and wait for the user; prefer -" + command);
        }
        return findings;
    }

    /// <summary>Rejects code that can never run safely inside one tracked operation.</summary>
    public static void EnsureRunnable(IReadOnlyList<LispFinding> findings)
    {
        var blocked = findings.Where(f => f.Severity == Blocked).ToArray();
        if (blocked.Length > 0) throw new CadFault(blocked[0].Code, string.Join("; ", blocked.Select(f => f.Detail)) + ". Ask the user to open or switch drawings; CAD MCP works on the assigned drawing only");
    }

    /// <summary>run, ask or deny for this code under the effective policy.</summary>
    public static string Decide(LispPolicyMode mode, IReadOnlyList<LispFinding> findings) => mode switch
    {
        LispPolicyMode.Deny => "deny",
        LispPolicyMode.Allow => "run",
        LispPolicyMode.AutoSafe => findings.Any(f => f.Severity == Risky) ? "ask" : "run",
        _ => "ask"
    };

    private enum TokenKind { None, Open, Close, Symbol, String }
    private readonly record struct Token(TokenKind Kind, string Text);
    private static List<Token> Tokenize(string code)
    {
        var tokens = new List<Token>();
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            if (char.IsWhiteSpace(c)) continue;
            if (c == '(') { tokens.Add(new(TokenKind.Open, "(")); continue; }
            if (c == ')') { tokens.Add(new(TokenKind.Close, ")")); continue; }
            if (c == ';')
            {
                if (i + 1 < code.Length && code[i + 1] == '|') { int end = code.IndexOf("|;", i + 2, StringComparison.Ordinal); i = end < 0 ? code.Length : end + 1; }
                else while (i < code.Length && code[i] != '\n') i++;
                continue;
            }
            if (c == '"')
            {
                var text = new System.Text.StringBuilder();
                for (i++; i < code.Length && code[i] != '"'; i++) { if (code[i] == '\\' && i + 1 < code.Length) i++; text.Append(code[i]); }
                tokens.Add(new(TokenKind.String, text.ToString())); continue;
            }
            int start = i;
            while (i < code.Length && !char.IsWhiteSpace(code[i]) && code[i] is not ('(' or ')' or '"' or ';')) i++;
            tokens.Add(new(TokenKind.Symbol, code[start..i])); i--;
        }
        return tokens;
    }
}

/// <summary>
/// User settings of CAD MCP in the per-user data folder. The AutoLISP policy is changed by the user
/// (AutoCAD command CADMCPLISP), never by an agent; CAD_MCP_LISP_POLICY overrides it for unattended runs.
/// </summary>
public static class CadSettings
{
    public static string DefaultPath => Path.Combine(Wire.DataRoot, "settings.json");
    /// <summary>Set from the confirmation dialog; lasts until AutoCAD restarts.</summary>
    public static LispPolicyMode? SessionLispPolicy { get; set; }

    public static LispPolicyMode StoredLispPolicy(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            if (!File.Exists(file)) return LispPolicyMode.Ask;
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return LispPolicy.Parse(document.RootElement.Text("lisp_policy"));
        }
        // A damaged or hand-edited file must never weaken the policy.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CadFault) { return LispPolicyMode.Ask; }
    }

    public static void StoreLispPolicy(LispPolicyMode mode, string? path = null)
    {
        var file = path ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        JsonObject settings;
        try { settings = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? new() : new(); }
        catch (JsonException) { settings = new(); }
        settings["lisp_policy"] = LispPolicy.Name(mode);
        var temp = file + ".tmp";
        File.WriteAllText(temp, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Portable.ReplaceFile(temp, file);
    }

    /// <summary>Environment override, else the stored choice, relaxed to AutoSafe only if the user chose that for this session.</summary>
    public static LispPolicyMode EffectiveLispPolicy(string? path = null)
    {
        if (Environment.GetEnvironmentVariable("CAD_MCP_LISP_POLICY") is { Length: > 0 } environment)
            try { return LispPolicy.Parse(environment); } catch (CadFault) { return LispPolicyMode.Ask; }
        var stored = StoredLispPolicy(path);
        return stored == LispPolicyMode.Ask && SessionLispPolicy == LispPolicyMode.AutoSafe ? LispPolicyMode.AutoSafe : stored;
    }
}
