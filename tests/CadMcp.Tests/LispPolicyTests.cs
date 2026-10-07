using CadMcp.Core;

namespace CadMcp.Tests;

[Collection("Environment")]
public sealed class LispPolicyTests
{
    private static string[] Codes(string code) => LispPolicy.Scan(code).Select(f => f.Code).ToArray();

    [Theory]
    [InlineData("(startapp \"cmd.exe\" \"/c del x\")", "LISP_EXTERNAL_PROCESS")]
    [InlineData("(apply 'startapp '(\"notepad\"))", "LISP_EXTERNAL_PROCESS")]
    [InlineData("(setq sh (vlax-create-object \"WScript.Shell\"))", "LISP_EXTERNAL_PROCESS")]
    [InlineData("(command-s \"_.SHELL\" \"dir\")", "LISP_EXTERNAL_PROCESS")]
    [InlineData("(command \"._NETLOAD\" \"c:/x.dll\")", "LISP_CODE_LOADING")]
    [InlineData("(load \"c:/tools/x.lsp\")", "LISP_CODE_LOADING")]
    [InlineData("(setq f (open \"c:/out.txt\" \"w\")) (write-line \"x\" f) (close f)", "LISP_FILE_WRITE")]
    [InlineData("(vl-registry-write \"HKEY_CURRENT_USER\\\\x\" \"\" \"1\")", "LISP_REGISTRY")]
    [InlineData("(eval (read \"(princ)\"))", "LISP_DYNAMIC_CODE")]
    [InlineData("(vlr-object-reactor nil nil nil)", "LISP_REACTOR")]
    [InlineData("(dos_delete \"c:/x\")", "LISP_EXTERNAL_PROCESS")]
    [InlineData("(command \"_.START\" \"cmd\")", "LISP_EXTERNAL_PROCESS")]
    [InlineData("(command \"DEL\" \"c:/x.dwg\")", "LISP_EXTERNAL_PROCESS")]
    [InlineData("(command \"_.SAVEAS\" \"2018\" \"c:/x.dwg\")", "LISP_FILE_WRITE")]
    [InlineData("(command-s \"_.-WBLOCK\" \"c:/x.dwg\" \"*\")", "LISP_FILE_WRITE")]
    [InlineData("(apply (car (atoms-family 0 '(\"startapp\"))) '(\"x\"))", "LISP_DYNAMIC_CODE")]
    [InlineData("(vlax-invoke-method doc 'SendCommand \"_.LINE 0,0 1,1  \")", "LISP_SEND_COMMAND")]
    [InlineData("(vla-postcommand doc \"_.LINE \")", "LISP_SEND_COMMAND")]
    [InlineData("(vlax-invoke (vla-get-activedocument (vlax-get-acad-object)) 'Close)", "LISP_DOCUMENT_SWITCH")]
    [InlineData("(vla-open dbx \"c:/x.dwg\")", "LISP_DOCUMENT_SWITCH")]
    [InlineData("(setvar \"SECURELOAD\" 0)", "LISP_REGISTRY")]
    [InlineData("(vla-runmacro acad \"x.dvb!Main\")", "LISP_CODE_LOADING")]
    public void Risky_constructs_are_found(string code, string expected)
    {
        Assert.Contains(expected, Codes(code));
        Assert.All(LispPolicy.Scan(code).Where(f => f.Code == expected), f => Assert.Equal(LispPolicy.Risky, f.Severity));
    }

    [Theory]
    [InlineData("(command-s \"_.LINE\" '(0 0) '(10 0) \"\")")]
    [InlineData("(princ \"startapp and (load) inside a string\")")]
    [InlineData("; (startapp \"x\") in a comment\n(princ)")]
    [InlineData(";| (load \"x\") |; (setq a 1)")]
    [InlineData("(command \"_-LAYER\" \"_M\" \"Сеть\" \"\")")]
    [InlineData("(setq loaded 1 reader 2)")]
    [InlineData("(setq doc (vlax-get-property (vlax-get-acad-object) 'ActiveDocument))")]
    [InlineData("(vlax-invoke-method (vla-get-modelspace doc) 'AddLine p1 p2)")]
    [InlineData("(setvar \"OSMODE\" 0) (setvar \"CLAYER\" \"0\")")]
    [InlineData("(command \"_.PEDIT\" pl \"_Edit\" \"_X\" \"\")")]
    public void Ordinary_drawing_code_has_no_findings(string code) => Assert.Empty(LispPolicy.Scan(code));

    [Theory]
    [InlineData("(command \"_.OPEN\" \"c:/other.dwg\")")]
    [InlineData("(command-s \"CLOSE\")")]
    [InlineData("(vla-activate doc)")]
    [InlineData("(vl-cmdf \"._QUIT\" \"_Y\")")]
    [InlineData("(command \"_.CLOSEALL\")")]
    [InlineData("(command \"QNEW\")")]
    [InlineData("(vla-put-activedocument acad doc)")]
    // The script's first (command) starts a command, so its first string cannot be an option keyword.
    [InlineData("(command \"_CLOSE\" \"_N\")")]
    [InlineData("(vl-cmdf \"QUIT\" \"_Y\")")]
    [InlineData("(vla-open (vla-get-documents (vlax-get-acad-object)) \"c:/x.dwg\")")]
    public void Switching_or_closing_drawings_is_rejected(string code)
    {
        var findings = LispPolicy.Scan(code);
        Assert.Contains(findings, f => f.Code == "LISP_DOCUMENT_SWITCH" && f.Severity == LispPolicy.Blocked);
        Assert.Equal("LISP_DOCUMENT_SWITCH", Assert.Throws<CadFault>(() => LispPolicy.EnsureRunnable(findings)).Code);
    }

    [Theory]
    [InlineData("(command \"_.PLINE\") (foreach p pts (command p)) (command \"_Close\")")]
    [InlineData("(command \"_.-LAYER\") (command \"_New\" \"Стены\") (command \"\")")]
    [InlineData("(command \"_.PEDIT\" pl) (command \"_Open\") (command \"_eXit\")")]
    public void Option_keywords_named_like_drawing_commands_are_not_blocked(string code)
    {
        // A (command) call can continue a command an earlier call left waiting: "_Close" is then the PLINE option.
        var findings = LispPolicy.Scan(code);
        Assert.DoesNotContain(findings, f => f.Severity == LispPolicy.Blocked);
        Assert.All(findings, f => Assert.Equal("LISP_DOCUMENT_SWITCH", f.Code));
        LispPolicy.EnsureRunnable(findings);
    }

    [Theory]
    [InlineData("(command \"CADMCPLISP\" \"_Allow\")")]
    [InlineData("(vla-sendcommand doc \"cadmcplisp _Allow \")")]
    [InlineData("(vlax-invoke-method doc 'SendCommand \"CADMCPLISP A \")")]
    public void Scripts_cannot_change_the_users_policy(string code) =>
        Assert.Equal("LISP_POLICY_CHANGE", Assert.Throws<CadFault>(() => LispPolicy.EnsureRunnable(LispPolicy.Scan(code))).Code);

    [Fact]
    public void Dialog_commands_are_notices_and_their_command_line_forms_are_not()
    {
        var findings = LispPolicy.Scan("(command \"_.DIMSTYLE\")");
        Assert.Equal(new[] { "LISP_DIALOG_COMMAND" }, findings.Select(f => f.Code));
        Assert.Equal(LispPolicy.Notice, findings[0].Severity);
        Assert.Empty(LispPolicy.Scan("(command \"_.-DIMSTYLE\" \"_R\" \"ISO-25\")"));
        LispPolicy.EnsureRunnable(findings);
    }

    [Fact]
    public void Decision_follows_the_policy()
    {
        var safe = LispPolicy.Scan("(command-s \"_.LINE\" '(0 0) '(1 1) \"\")");
        var risky = LispPolicy.Scan("(startapp \"x\")");
        var notice = LispPolicy.Scan("(command \"_.DIMSTYLE\")");
        Assert.Equal("ask", LispPolicy.Decide(LispPolicyMode.Ask, safe));
        Assert.Equal("run", LispPolicy.Decide(LispPolicyMode.AutoSafe, safe));
        Assert.Equal("run", LispPolicy.Decide(LispPolicyMode.AutoSafe, notice));
        Assert.Equal("ask", LispPolicy.Decide(LispPolicyMode.AutoSafe, risky));
        Assert.Equal("run", LispPolicy.Decide(LispPolicyMode.Allow, risky));
        Assert.Equal("deny", LispPolicy.Decide(LispPolicyMode.Deny, safe));
        foreach (var mode in Enum.GetValues<LispPolicyMode>()) Assert.Equal(mode, LispPolicy.Parse(LispPolicy.Name(mode)));
        Assert.Equal("INVALID_LISP_POLICY", Assert.Throws<CadFault>(() => LispPolicy.Parse("sometimes")).Code);
    }

    [Fact]
    public void Settings_persist_fail_closed_and_yield_to_the_environment()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cadmcp-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "settings.json");
        var previous = Environment.GetEnvironmentVariable("CAD_MCP_LISP_POLICY");
        try
        {
            Environment.SetEnvironmentVariable("CAD_MCP_LISP_POLICY", null);
            CadSettings.SessionLispPolicy = null;
            Assert.Equal(LispPolicyMode.Ask, CadSettings.EffectiveLispPolicy(path));
            CadSettings.StoreLispPolicy(LispPolicyMode.Allow, path);
            Assert.Equal(LispPolicyMode.Allow, CadSettings.StoredLispPolicy(path));
            CadSettings.StoreLispPolicy(LispPolicyMode.Ask, path);
            CadSettings.RelaxForSession(path);
            Assert.Equal(LispPolicyMode.AutoSafe, CadSettings.EffectiveLispPolicy(path));
            // Storing a policy again, here or from a chat panel in another process, ends the relaxation.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            Assert.Equal(LispPolicyMode.Ask, CadSettings.EffectiveLispPolicy(path));
            Assert.Null(CadSettings.SessionLispPolicy);
            CadSettings.RelaxForSession(path);
            CadSettings.StoreLispPolicy(LispPolicyMode.Deny, path);
            Assert.Equal(LispPolicyMode.Deny, CadSettings.EffectiveLispPolicy(path));
            File.WriteAllText(path, "{ broken");
            Assert.Equal(LispPolicyMode.Ask, CadSettings.StoredLispPolicy(path));
            File.WriteAllText(path, "{\"lisp_policy\":\"whatever\"}");
            Assert.Equal(LispPolicyMode.Ask, CadSettings.StoredLispPolicy(path));
            File.WriteAllText(path, "{\"lisp_policy\":1}");
            Assert.Equal(LispPolicyMode.Ask, CadSettings.StoredLispPolicy(path));
            Environment.SetEnvironmentVariable("CAD_MCP_LISP_POLICY", "allow");
            Assert.Equal(LispPolicyMode.Allow, CadSettings.EffectiveLispPolicy(path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CAD_MCP_LISP_POLICY", previous);
            CadSettings.SessionLispPolicy = null;
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Wrapper_suspends_osnaps_and_dialogs_and_restores_them()
    {
        var script = LispScript.Wrap("operation_7");
        Assert.Contains("16384", script);
        Assert.Contains("(\"FILEDIA\" . 0)", script);
        Assert.Contains("(\"CMDDIA\" . 0)", script);
        const string restore = "(foreach cadmcp:item cadmcp:saved";
        Assert.Contains(restore, script);
        Assert.True(script.IndexOf(restore, StringComparison.Ordinal) > script.IndexOf("(eval (read cadmcp:code))", StringComparison.Ordinal), "Settings must be restored after the script");
        // A value the script set itself is kept, and an error in the restore cannot skip cadmcpfinish.
        // After an error every variable is restored; a script that completed keeps the values it set itself.
        Assert.Contains("(if (or (vl-catch-all-error-p cadmcp:value) (equal (getvar (car cadmcp:item)) (cdr (assoc (car cadmcp:item) cadmcp:set))))", script);
        Assert.Contains("(vl-catch-all-apply (function (lambda () " + restore, script);
        // Dynamically scoped locals visible to the script carry a prefix no ordinary script uses.
        foreach (var local in System.Text.RegularExpressions.Regex.Match(script, @"\(lambda \(/ ([^)]*)\)").Groups[1].Value.Split(' '))
            Assert.StartsWith("cadmcp:", local);
    }
}
