namespace CadMcp.Core;

/// <summary>Stages on the target volume, retains the old disk file, and refuses changed inputs.</summary>
public sealed class DwgFileReplacement
{
    public string Target { get; }
    public string Backup { get; }
    private readonly string staged, outputHash;
    private readonly string? expectedTargetHash;
    public bool Applied { get; private set; }
    public DwgFileReplacement(string output, string outputHash, string target, string? expectedTargetHash)
    {
        Target = Path.GetFullPath(target); this.outputHash = outputHash; this.expectedTargetHash = expectedTargetHash;
        LocalWorkPolicy.RequireLocal(Target);
        if (File.Exists(Target) && (File.GetAttributes(Target) & FileAttributes.ReadOnly) != 0) throw new IOException("Original DWG is read-only; output remains separate");
        string token = Guid.NewGuid().ToString("N");
        Backup = Target + ".cadmcp-before-" + token + ".dwg";
        staged = Path.Combine(Path.GetDirectoryName(Target)!, ".cadmcp-" + token + ".dwg");
        RequireUnchanged();
        if (IsolatedCSharpJob.HashFile(output) != outputHash) throw new CadFault("CSHARP_OUTPUT_CHANGED", "Output DWG hash changed before replacement");
        File.Copy(output, staged, overwrite: false);
        if (IsolatedCSharpJob.HashFile(staged) != outputHash) throw new IOException("Staged DWG hash mismatch");
    }
    private void RequireUnchanged()
    {
        if (expectedTargetHash is null ? File.Exists(Target) : !File.Exists(Target) || IsolatedCSharpJob.HashFile(Target) != expectedTargetHash)
            throw new CadFault("CSHARP_SOURCE_FILE_CHANGED", "Original disk file changed; result is retained separately and must not overwrite it");
    }
    public void Apply()
    {
        RequireUnchanged();
        if (expectedTargetHash is null) File.Move(staged, Target, overwrite: false);
        else File.Replace(staged, Target, Backup);
        Applied = true;
        if (expectedTargetHash is not null && IsolatedCSharpJob.HashFile(Backup) != expectedTargetHash)
        { Rollback(); throw new CadFault("CSHARP_SOURCE_FILE_CHANGED", "Original file changed during replacement; its disk contents were restored"); }
    }
    public void Rollback()
    {
        if (!Applied) return;
        if (!File.Exists(Target) || IsolatedCSharpJob.HashFile(Target) != outputHash)
            throw new IOException("Replacement target changed again; refusing to overwrite it during recovery. Inspect retained files.");
        if (expectedTargetHash is null) File.Delete(Target);
        else
        {
            string restore = staged + ".restore"; File.Copy(Backup, restore, overwrite: false);
            File.Replace(restore, Target, Target + ".cadmcp-rejected-" + Guid.NewGuid().ToString("N") + ".dwg");
        }
        Applied = false;
    }
}

/// <summary>Cancellation can withdraw a pending UI action, never interrupt a file replacement already in progress.</summary>
public sealed class DeferredCadAction<T>(Func<T> action)
{
    private int state;
    private readonly TaskCompletionSource<T> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<T> Task => source.Task;
    public void CancelPending() { if (Interlocked.CompareExchange(ref state, 2, 0) == 0) source.TrySetCanceled(); }
    public void Run()
    {
        if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
        try { source.TrySetResult(action()); } catch (Exception error) { source.TrySetException(error); }
        finally { Interlocked.Exchange(ref state, 3); }
    }
}
