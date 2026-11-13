using CadMcp.Core;

namespace CadMcp.TestCli;

internal static class IsolatedCSharpFixture
{
    internal static async Task<int> Run(string mode)
    {
        var job = IsolatedCSharpJob.Load(Environment.GetEnvironmentVariable(IsolatedCSharpJob.EnvironmentKey)!);
        job.Phase("running_script");
        if (mode == "hang") { await Task.Delay(Timeout.Infinite); return 0; }
        if (mode == "crash") return unchecked((int)0xc0000005);
        if (mode == "missing") return 0;
        File.WriteAllText(job.FilePath("result.dwg"), "AC1032 isolated test fixture");
        var receipt = new IsolatedCSharpReceipt(job.Request.RunId, mode == "wrong_identity" ? "wrong" : job.Request.Nonce,
            job.Request.OperationId, Environment.ProcessId, mode == "managed_failure" ? "failed" : "completed", job.Request.SourceSha256,
            job.Request.InputSha256, mode == "wrong_hash" ? "wrong" : IsolatedCSharpJob.HashFile(job.FilePath("result.dwg")),
            new FileInfo(job.FilePath("result.dwg")).Length, Wire.Element(new { test = true }),
            mode == "managed_failure" ? new("CSHARP_EXECUTION_FAILED", "Fixture exception") : null);
        job.Write("result.json", receipt); job.Phase("completed");
        return mode == "receipt_then_crash" ? unchecked((int)0xc0000005) : 0;
    }
}
