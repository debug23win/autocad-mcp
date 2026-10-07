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
        ["vla-postcommand"] = ("LISP_SEND_COMMAND", "runs command text that cannot be reviewed in advance"),
        ["vla-open"] = ("LISP_DOCUMENT_SWITCH", "opens a drawing (harmless only for an ObjectDBX side database)"),
        ["vla-save"] = ("LISP_FILE_WRITE", "saves a drawing file"),
        ["vla-saveas"] = ("LISP_FILE_WRITE", "saves a drawing under another name"),
        ["vla-wblock"] = ("LISP_FILE_WRITE", "writes objects to a drawing file"),
        ["vla-export"] = ("LISP_FILE_WRITE", "writes an export file"),
        ["vla-runmacro"] = ("LISP_CODE_LOADING", "runs a VBA macro"),
        ["vla-loadarx"] = ("LISP_CODE_LOADING", "loads a native ARX module"),
        ["vla-loaddvb"] = ("LISP_CODE_LOADING", "loads a VBA project"),
        ["vla-eval"] = ("LISP_DYNAMIC_CODE", "evaluates VBA code built at run time"),
        ["vlax-import-type-library"] = ("LISP_CODE_LOADING", "imports functions of an external type library"),
        ["vlax-add-cmd"] = ("LISP_CODE_LOADING", "registers a command that runs code later"),
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
        ["atoms-family"] = ("LISP_DYNAMIC_CODE", "looks up functions by name at run time"),
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
        ["VBASTMT"] = ("LISP_CODE_LOADING", "runs a VBA statement"),
        ["CUILOAD"] = ("LISP_CODE_LOADING", "loads a customization file that can contain macros"),
        ["MENULOAD"] = ("LISP_CODE_LOADING", "loads a customization file that can contain macros"),
        // External commands defined in acad.pgp start operating-system programs.
        ["START"] = ("LISP_EXTERNAL_PROCESS", "starts an operating-system program (acad.pgp)"),
        ["DEL"] = ("LISP_EXTERNAL_PROCESS", "deletes files through the operating system (acad.pgp)"),
        ["DIR"] = ("LISP_EXTERNAL_PROCESS", "runs an operating-system command (acad.pgp)"),
        ["TYPE"] = ("LISP_EXTERNAL_PROCESS", "runs an operating-system command (acad.pgp)"),
        ["CATALOG"] = ("LISP_EXTERNAL_PROCESS", "runs an operating-system command (acad.pgp)"),
        ["EDIT"] = ("LISP_EXTERNAL_PROCESS", "starts an operating-system editor (acad.pgp)"),
        ["EXPLORER"] = ("LISP_EXTERNAL_PROCESS", "starts Windows Explorer (acad.pgp)"),
        ["NOTEPAD"] = ("LISP_EXTERNAL_PROCESS", "starts Notepad (acad.pgp)"),
        ["PBRUSH"] = ("LISP_EXTERNAL_PROCESS", "starts Paint (acad.pgp)"),
        ["SECURELOAD"] = ("LISP_REGISTRY", "changes AutoCAD's protection against loading untrusted code"),
        ["TRUSTEDPATHS"] = ("LISP_REGISTRY", "changes the folders AutoCAD trusts for code"),
        ["SAVE"] = ("LISP_FILE_WRITE", "writes a drawing file"),
        ["SAVEAS"] = ("LISP_FILE_WRITE", "writes a drawing file"),
        ["QSAVE"] = ("LISP_FILE_WRITE", "overwrites the drawing file"),
        ["SAVEALL"] = ("LISP_FILE_WRITE", "overwrites every open drawing file"),
        ["WBLOCK"] = ("LISP_FILE_WRITE", "writes a drawing file"),
        ["EXPORT"] = ("LISP_FILE_WRITE", "writes an export file"),
        ["EXPORTPDF"] = ("LISP_FILE_WRITE", "writes a PDF file"),
        ["EXPORTLAYOUT"] = ("LISP_FILE_WRITE", "writes a drawing file"),
        ["DXFOUT"] = ("LISP_FILE_WRITE", "writes a DXF file"),
        ["PLOT"] = ("LISP_FILE_WRITE", "plots to a device or file"),
        ["PUBLISH"] = ("LISP_FILE_WRITE", "writes plot files"),
        ["ETRANSMIT"] = ("LISP_FILE_WRITE", "writes a transmittal package"),
        ["ARCHIVE"] = ("LISP_FILE_WRITE", "writes a sheet set archive")
    };
    // Security-relevant system variables changed through setvar.
    private static readonly Dictionary<string, (string Code, string Detail)> RiskyVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SECURELOAD"] = ("LISP_REGISTRY", "changes AutoCAD's protection against loading untrusted code"),
        ["TRUSTEDPATHS"] = ("LISP_REGISTRY", "changes the folders AutoCAD trusts for code"),
        ["TRUSTEDDOMAINS"] = ("LISP_REGISTRY", "changes the domains AutoCAD trusts for code"),
        ["ACADLSPASDOC"] = ("LISP_CODE_LOADING", "loads acad.lsp into every drawing"),
        ["LISPSYS"] = ("LISP_REGISTRY", "changes the AutoLISP engine")
    };
    // Methods reached through vlax-invoke, vlax-put-property and their relatives; the object is unknown before
    // the script runs, so these are risks for the user to judge, never blocks.
    private static readonly Dictionary<string, (string Code, string Detail)> RiskyMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Close"] = ("LISP_DOCUMENT_SWITCH", "may close a drawing"),
        ["Activate"] = ("LISP_DOCUMENT_SWITCH", "may switch the active drawing"),
        ["ActiveDocument"] = ("LISP_DOCUMENT_SWITCH", "may switch the active drawing"),
        ["Open"] = ("LISP_DOCUMENT_SWITCH", "may open a drawing"),
        ["Quit"] = ("LISP_DOCUMENT_SWITCH", "may quit AutoCAD"),
        ["SendCommand"] = ("LISP_SEND_COMMAND", "runs command text that cannot be reviewed in advance"),
        ["PostCommand"] = ("LISP_SEND_COMMAND", "runs command text that cannot be reviewed in advance"),
        ["Save"] = ("LISP_FILE_WRITE", "may save a file"),
        ["SaveAs"] = ("LISP_FILE_WRITE", "may save a file"),
        ["Wblock"] = ("LISP_FILE_WRITE", "writes a drawing file"),
        ["Export"] = ("LISP_FILE_WRITE", "writes an export file"),
        ["RunMacro"] = ("LISP_CODE_LOADING", "runs a VBA macro"),
        ["LoadArx"] = ("LISP_CODE_LOADING", "loads a native ARX module"),
        ["LoadDVB"] = ("LISP_CODE_LOADING", "loads a VBA project"),
        ["Eval"] = ("LISP_DYNAMIC_CODE", "evaluates code built at run time"),
        ["Run"] = ("LISP_EXTERNAL_PROCESS", "may start a program"),
        ["Exec"] = ("LISP_EXTERNAL_PROCESS", "may start a program"),
        ["ShellExecute"] = ("LISP_EXTERNAL_PROCESS", "may start a program")
    };
    private static readonly HashSet<string> VlaxCalls = new(StringComparer.OrdinalIgnoreCase)
    { "vlax-invoke", "vlax-invoke-method", "vlax-put-property", "vlax-put" };
    // Switching or closing drawings from inside the running script breaks the operation's document
    // tracking and can crash AutoCAD; drawings are opened and activated by the user.
    private static readonly HashSet<string> DocumentCommands = new(StringComparer.OrdinalIgnoreCase)
    { "OPEN", "NEW", "QNEW", "CLOSE", "CLOSEALL", "QUIT", "EXIT", "RECOVER", "RECOVERALL", "PARTIALOPEN" };
    // These names are also option keywords (PLINE Close, PEDIT Open and eXit, -LAYER New): a (command) call can
    // continue a command an earlier call left waiting. They block only where they must be a command name.
    private static readonly HashSet<string> OptionLikeDocumentCommands = new(StringComparer.OrdinalIgnoreCase) { "OPEN", "NEW", "CLOSE", "QUIT", "EXIT" };
    private static readonly HashSet<string> DocumentFunctions = new(StringComparer.OrdinalIgnoreCase) { "vla-close", "vla-activate", "vla-put-activedocument", "vla-quit" };
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
        // The wrapper completes UNDO Begin before the script, so the script's first (command) call starts a command.
        bool firstCommand = true;
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            // The policy belongs to the user; a script that changes it (in a command, SendCommand text or a
            // string built elsewhere) is never run.
            if (token.Text.Contains("CADMCPLISP", StringComparison.OrdinalIgnoreCase))
                Add("LISP_POLICY_CHANGE", Blocked, "CADMCPLISP: only the user may change the AutoLISP policy");
            if (token.Kind != TokenKind.Symbol) continue;
            string name = token.Text.TrimStart('\'');
            bool call = i > 0 && tokens[i - 1].Kind == TokenKind.Open;
            if (RiskyFunctions.TryGetValue(name, out var risky)) Add(risky.Code, Risky, name + ": " + risky.Detail);
            if (name.StartsWith("dos_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("acet-sys-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("acet-file-", StringComparison.OrdinalIgnoreCase))
                Add("LISP_EXTERNAL_PROCESS", Risky, name + ": operating-system extension function");
            if (name.StartsWith("vlr-", StringComparison.OrdinalIgnoreCase)) Add("LISP_REACTOR", Risky, name + ": installs a reactor that runs code later");
            if (DocumentFunctions.Contains(name)) Add("LISP_DOCUMENT_SWITCH", Blocked, name + ": opens, closes or activates a drawing");
            // vla-open on the Documents collection opens a drawing in the editor; on an ObjectDBX document it is a side read.
            if (call && name.Equals("vla-open", StringComparison.OrdinalIgnoreCase) && Argument(tokens, i, 1) is { Kind: TokenKind.Open } && i + 3 < tokens.Count
                && tokens[i + 2].Kind == TokenKind.Symbol && tokens[i + 2].Text.Equals("vla-get-documents", StringComparison.OrdinalIgnoreCase))
                Add("LISP_DOCUMENT_SWITCH", Blocked, "vla-open: opens a drawing in the editor");
            if (call && VlaxCalls.Contains(name) && Argument(tokens, i, 2) is { } method && method.Kind is TokenKind.Symbol or TokenKind.String
                && RiskyMethods.TryGetValue(method.Text.TrimStart('\''), out var riskyMethod))
                Add(riskyMethod.Code, Risky, name + " " + method.Text.TrimStart('\'') + ": " + riskyMethod.Detail);
            if (call && name.Equals("setvar", StringComparison.OrdinalIgnoreCase) && Argument(tokens, i, 1) is { Kind: TokenKind.String } variable
                && RiskyVariables.TryGetValue(variable.Text.Trim(), out var riskyVariable))
                Add(riskyVariable.Code, Risky, "setvar " + variable.Text.Trim() + ": " + riskyVariable.Detail);
            if (!CommandFunctions.Contains(name) || !call) continue;
            bool first = firstCommand;
            firstCommand = false;
            var argument = i + 1 < tokens.Count ? tokens[i + 1] : default;
            if (argument.Kind != TokenKind.String) continue;
            string command = argument.Text.Trim();
            string prefix = command[..(command.Length - command.TrimStart('_', '.', '+', '-').Length)];
            bool commandLine = prefix.Contains('-');
            command = command.TrimStart('_', '.', '+', '-').Split(' ', '\t')[0];
            if (command.Length == 0) continue;
            // command-s takes a whole command, and the script's first (command) call starts one, so their first string
            // is a command name; so is any string with a dot (built-in command) or hyphen (command-line form) prefix.
            if (DocumentCommands.Contains(command))
            {
                bool commandName = first || name.Equals("command-s", StringComparison.OrdinalIgnoreCase) || prefix.Contains('.') || prefix.Contains('-') || !OptionLikeDocumentCommands.Contains(command);
                Add("LISP_DOCUMENT_SWITCH", commandName ? Blocked : Risky, command + ": opens, closes or switches drawings" + (commandName ? "" : " when used as a command; harmless as an option keyword"));
            }
            if (RiskyCommands.TryGetValue(command, out var riskyCommand)) Add(riskyCommand.Code, Risky, command + ": " + riskyCommand.Detail);
            if (!commandLine && DialogCommands.Contains(command)) Add("LISP_DIALOG_COMMAND", Notice, command + ": may open a dialog and wait for the user; prefer -" + command);
        }
        return findings;
    }

    /// <summary>The n-th argument (1-based) of the call whose function symbol is at index <paramref name="call"/>: a single token or the opening parenthesis of a nested form.</summary>
    private static Token? Argument(List<Token> tokens, int call, int n)
    {
        int depth = 0, index = 0;
        for (int j = call + 1; j < tokens.Count; j++)
        {
            var token = tokens[j];
            if (token.Kind == TokenKind.Close) { if (depth == 0) return null; depth--; continue; }
            if (depth == 0 && ++index == n) return token;
            if (token.Kind == TokenKind.Open) depth++;
        }
        return null;
    }

    /// <summary>Rejects code that can never run safely inside one tracked operation.</summary>
    public static void EnsureRunnable(IReadOnlyList<LispFinding> findings)
    {
        var blocked = findings.Where(f => f.Severity == Blocked).ToArray();
        if (blocked.Length == 0) return;
        string advice = blocked[0].Code == "LISP_POLICY_CHANGE" ? "Only the user changes the AutoLISP policy (command CADMCPLISP or the chat panel)"
            : "Ask the user to open or switch drawings; CAD MCP works on the assigned drawing only";
        throw new CadFault(blocked[0].Code, string.Join("; ", blocked.Select(f => f.Detail)) + ". " + advice);
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
    private static LispPolicyMode? sessionPolicy;
    private static long? sessionStamp;
    /// <summary>
    /// Set from the confirmation dialog; lasts until AutoCAD restarts or the user stores a policy again. The chat
    /// panel may run in another process, so a stored choice is noticed by the settings file changing.
    /// </summary>
    public static LispPolicyMode? SessionLispPolicy { get => sessionPolicy; set { sessionPolicy = value; sessionStamp = null; } }
    /// <summary>The user's "do not ask until restart": AutoSafe until AutoCAD restarts or a policy is stored again.</summary>
    public static void RelaxForSession(string? path = null) { sessionPolicy = LispPolicyMode.AutoSafe; sessionStamp = Stamp(path); }
    private static long Stamp(string? path)
    {
        try { var file = path ?? DefaultPath; return File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks : 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return -1; }
    }

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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CadFault or InvalidOperationException) { return LispPolicyMode.Ask; }
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
        if (sessionPolicy is not null && sessionStamp is { } stamp && Stamp(path) != stamp) sessionPolicy = null;
        return stored == LispPolicyMode.Ask && sessionPolicy == LispPolicyMode.AutoSafe ? LispPolicyMode.AutoSafe : stored;
    }
}
