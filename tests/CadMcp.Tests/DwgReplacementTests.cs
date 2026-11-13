using CadMcp.Core;

namespace CadMcp.Tests;

public sealed class DwgReplacementTests
{
    [Fact]
    public void Atomic_replacement_preserves_old_disk_file_and_can_restore_it()
    {
        using var folder = new TempFolder(); string old = folder.File("main.dwg"), output = folder.File("result.dwg");
        File.WriteAllText(old, "original disk data"); File.WriteAllText(output, "verified result");
        string hash = IsolatedCSharpJob.HashFile(old);
        var replacement = new DwgFileReplacement(output, IsolatedCSharpJob.HashFile(output), old, hash);
        Assert.Equal("original disk data", File.ReadAllText(old));
        replacement.Apply(); Assert.Equal("verified result", File.ReadAllText(old)); Assert.Equal("original disk data", File.ReadAllText(replacement.Backup));
        replacement.Rollback(); Assert.Equal(hash, IsolatedCSharpJob.HashFile(old)); Assert.True(File.Exists(replacement.Backup));
        Assert.Equal("verified result", File.ReadAllText(output));
    }
    [Fact]
    public void Changed_original_or_result_is_never_overwritten()
    {
        using var folder = new TempFolder(); string old = folder.File("main.dwg"), output = folder.File("result.dwg");
        File.WriteAllText(old, "before"); File.WriteAllText(output, "result");
        var replacement = new DwgFileReplacement(output, IsolatedCSharpJob.HashFile(output), old, IsolatedCSharpJob.HashFile(old));
        File.WriteAllText(old, "new user changes");
        Assert.Equal("CSHARP_SOURCE_FILE_CHANGED", Assert.Throws<CadFault>(replacement.Apply).Code); Assert.Equal("new user changes", File.ReadAllText(old));
        Assert.Equal("CSHARP_OUTPUT_CHANGED", Assert.Throws<CadFault>(() => new DwgFileReplacement(output, "wrong", old, IsolatedCSharpJob.HashFile(old))).Code);
    }
    [Fact]
    public async Task Pending_action_can_be_withdrawn_but_a_started_replacement_is_not_interrupted_or_repeated()
    {
        int count = 0;
        var pending = new DeferredCadAction<int>(() => ++count); pending.CancelPending(); pending.Run();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.Task); Assert.Equal(0, count);
        DeferredCadAction<int>? started = null;
        started = new(() => { started!.CancelPending(); return ++count; });
        started.Run(); started.Run(); Assert.Equal(1, await started.Task); Assert.Equal(1, count);
    }
}
