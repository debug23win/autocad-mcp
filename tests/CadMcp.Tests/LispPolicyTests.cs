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
    public void Ordinary_drawing_code_has_no_findings(string code) => Assert.Empty(LispPolicy.Scan(code));

    [Theory]
    [InlineData("(command \"_.OPEN\" \"c:/other.dwg\")")]
    [InlineData("(command-s \"CLOSE\")")]
    [InlineData("(vla-activate doc)")]
    [InlineData("(vl-cmdf \"._QUIT\" \"_Y\")")]
    public void Switching_or_closing_drawings_is_rejected(string code)
    {
        var findings = LispPolicy.Scan(code);
        Assert.Contains(findings, f => f.Code == "LISP_DOCUMENT_SWITCH" && f.Severity == LispPolicy.Blocked);
        Assert.Equal("LISP_DOCUMENT_SWITCH", Assert.Throws<CadFault>(() => LispPolicy.EnsureRunnable(findings)).Code);
    }

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
            CadSettings.SessionLispPolicy = LispPolicyMode.AutoSafe;
            Assert.Equal(LispPolicyMode.AutoSafe, CadSettings.EffectiveLispPolicy(path));
            CadSettings.StoreLispPolicy(LispPolicyMode.Deny, path);
            Assert.Equal(LispPolicyMode.Deny, CadSettings.EffectiveLispPolicy(path));
            File.WriteAllText(path, "{ broken");
            Assert.Equal(LispPolicyMode.Ask, CadSettings.StoredLispPolicy(path));
            File.WriteAllText(path, "{\"lisp_policy\":\"whatever\"}");
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
        Assert.Contains("\"FILEDIA\" 0", script);
        Assert.Contains("\"CMDDIA\" 0", script);
        Assert.Contains("(foreach item saved", script);
        Assert.True(script.IndexOf("(foreach item saved", StringComparison.Ordinal) > script.IndexOf("(eval (read code))", StringComparison.Ordinal), "Settings must be restored after the script");
    }
}
