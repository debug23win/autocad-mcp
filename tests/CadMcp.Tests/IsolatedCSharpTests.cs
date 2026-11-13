using System.Diagnostics;
using CadMcp.Core;

namespace CadMcp.Tests;

public sealed class IsolatedCSharpTests
{
    private static IsolatedCSharpJob Job(TempFolder folder, TimeSpan? timeout = null)
    {
        string input = folder.File("before.dwg"); File.WriteAllText(input, "AC1032 original test input");
        return IsolatedCSharpJob.Create(folder.Path, input, IsolatedCSharpJob.HashFile(input), [1, 2, 3], "SOURCE", "isolated-test", timeout ?? TimeSpan.FromSeconds(20));
    }
    private static ProcessStartInfo Start(string mode)
    {
        var start = new ProcessStartInfo(TestEnvironment.FakeCli) { UseShellExecute = false };
        start.ArgumentList.Add("--isolated-csharp-test"); start.ArgumentList.Add(mode); return start;
    }
    [Fact]
    public async Task Success_requires_a_different_process_and_matching_saved_output_receipt()
    {
        using var folder = new TempFolder(); var job = Job(folder);
        string before = IsolatedCSharpJob.HashFile(folder.File("before.dwg"));
        var outcome = await job.RunAsync(Start("success"), default);
        Assert.True(outcome.Success, outcome.Error?.Message); Assert.NotEqual(Environment.ProcessId, outcome.ProcessId);
        Assert.Equal(0, outcome.ExitCode); Assert.False(outcome.ChildOutcomeUnknown);
        Assert.Equal(before, IsolatedCSharpJob.HashFile(folder.File("before.dwg")));
        Assert.Equal(outcome.Receipt!.OutputSha256, IsolatedCSharpJob.HashFile(job.FilePath("result.dwg")));
        Assert.True(File.Exists(job.FilePath("started.json")));
        var replay = await job.RunAsync(Start("success"), default);
        Assert.False(replay.Success); Assert.Null(replay.ProcessId); // launch.claim disallows a second execution.
    }
    [Theory]
    [InlineData("crash", "CSHARP_ISOLATED_PROCESS_EXITED")]
    [InlineData("receipt_then_crash", "CSHARP_ISOLATED_PROCESS_EXITED")]
    [InlineData("missing", "CSHARP_ISOLATED_RECEIPT_MISSING")]
    [InlineData("wrong_identity", "CSHARP_ISOLATION_FAILED")]
    [InlineData("wrong_hash", "CSHARP_ISOLATION_FAILED")]
    [InlineData("managed_failure", "CSHARP_EXECUTION_FAILED")]
    public async Task Abnormal_exit_missing_or_invalid_evidence_never_reports_success(string mode, string code)
    {
        using var folder = new TempFolder(); var job = Job(folder);
        var result = await job.RunAsync(Start(mode), default);
        Assert.False(result.Success); Assert.Equal(code, result.Error!.Code); Assert.True(result.ChildOutcomeUnknown);
        if (mode.Contains("crash")) Assert.Equal("0xC0000005", result.ExitCodeHex);
        Assert.True(File.Exists(job.FilePath("phase.json"))); Assert.True(File.Exists(job.FilePath("request.json")));
    }
    [Fact]
    public async Task Timeout_stops_only_the_owned_child_and_keeps_other_processes_alive()
    {
        using var folder = new TempFolder();
        var start = new ProcessStartInfo(TestEnvironment.FakeCli) { UseShellExecute = false }; start.ArgumentList.Add("--isolation-bystander");
        using var other = Process.Start(start)!;
        try
        {
            var job = Job(folder, TimeSpan.FromSeconds(2));
            var result = await job.RunAsync(Start("hang"), default);
            Assert.Equal("CSHARP_ISOLATED_TIMEOUT", result.Error!.Code); Assert.False(result.Success);
            Assert.False(result.ProcessStillRunning); Assert.False(other.HasExited);
            Assert.Equal(Environment.ProcessId, job.Request.ParentProcessId);
        }
        finally { if (!other.HasExited) other.Kill(); await other.WaitForExitAsync(); }
    }
    [Fact]
    public void Changed_checkpoint_blocks_launch_and_AutoCAD_arguments_are_separate_tokens()
    {
        using var folder = new TempFolder(); string input = folder.File("before.dwg"); File.WriteAllText(input, "DWG");
        Assert.Equal("CSHARP_ISOLATION_INPUT_CHANGED", Assert.Throws<CadFault>(() => IsolatedCSharpJob.Create(folder.Path, input, "wrong", [1], "s", "op", TimeSpan.FromSeconds(10))).Code);
        var job = Job(folder); string exe = folder.File("acad.exe"), plugin = folder.File("plugin with spaces.dll"); File.WriteAllText(exe, "fixture"); File.WriteAllText(plugin, "fixture");
        var start = job.AutoCADStart(exe, plugin, [exe, "/product", "MAP", "/language", "ru-RU", "user.dwg"]);
        Assert.Equal(new[] { "/product", "MAP", "/language", "ru-RU", "/nologo", "/b", job.FilePath("startup.scr") }, start.ArgumentList);
        Assert.False(start.UseShellExecute); Assert.DoesNotContain("SECURELOAD", File.ReadAllText(job.FilePath("startup.scr")));
        Assert.DoesNotContain("user.dwg", string.Join(' ', start.ArgumentList));
    }
    [Theory]
    [InlineData("success", null)]
    [InlineData("crash", "LISP_ISOLATED_PROCESS_EXITED")]
    [InlineData("receipt_then_crash", "LISP_ISOLATED_PROCESS_EXITED")]
    [InlineData("wrong_hash", "LISP_ISOLATION_FAILED")]
    public async Task Lisp_uses_the_same_single_execution_and_verified_output_gate(string mode, string? error)
    {
        using var folder = new TempFolder();
        string input = folder.File("before.dwg"); File.WriteAllText(input, "original");
        var script = System.Text.Encoding.UTF8.GetBytes("(princ)");
        var job = IsolatedCSharpJob.Create(folder.Path, input, IsolatedCSharpJob.HashFile(input), script,
            "LISP-SOURCE", "lisp-test", TimeSpan.FromSeconds(20), "lisp");
        Assert.Equal(script, File.ReadAllBytes(job.FilePath("script.lsp")));
        Assert.False(File.Exists(job.FilePath("script.dll")));
        var outcome = await job.RunAsync(Start(mode), default);
        Assert.Equal(error is null, outcome.Success); Assert.Equal(error, outcome.Error?.Code);
        Assert.Equal("original", File.ReadAllText(input));
        Assert.NotEqual(Environment.ProcessId, outcome.ProcessId);
        Assert.False((await job.RunAsync(Start("success"), default)).Success);
    }
    [Fact]
    public async Task Lisp_tampering_is_rejected_in_the_child_before_execution()
    {
        using var folder = new TempFolder(); string input = folder.File("before.dwg"); File.WriteAllText(input, "original");
        var job = IsolatedCSharpJob.Create(folder.Path, input, IsolatedCSharpJob.HashFile(input), [1], "s", "lisp-test", TimeSpan.FromSeconds(20), "lisp");
        File.WriteAllText(job.FilePath("script.lsp"), "changed");
        var outcome = await job.RunAsync(Start("success"), default);
        Assert.False(outcome.Success); Assert.Null(outcome.Receipt);
        Assert.False(File.Exists(job.FilePath("result.dwg")));
        Assert.Equal("prepared", outcome.LastPhase);
    }
}
