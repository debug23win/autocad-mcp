using CadMcp.Core;

namespace CadMcp.Tests;

public sealed class LocalWorkPolicyTests
{
    [Fact]
    public void Consumer_business_and_custom_sync_roots_are_blocked_without_prefix_false_positives()
    {
        using var folder = new TempFolder(); string custom = folder.File("SyncedCompany");
        Assert.False(LocalWorkPolicy.IsOneDrive(Path.GetPathRoot(folder.Path)!, []));
        Assert.True(LocalWorkPolicy.IsOneDrive(Path.Combine(folder.Path, "OneDrive", "a.dwg"), []));
        Assert.True(LocalWorkPolicy.IsOneDrive(Path.Combine(folder.Path, "OneDrive - Company", "a.dwg"), []));
        Assert.True(LocalWorkPolicy.IsOneDrive(Path.Combine(custom, "a.dwg"), [custom]));
        Assert.False(LocalWorkPolicy.IsOneDrive(custom + "Local/a.dwg", [custom]));
        Assert.False(LocalWorkPolicy.IsOneDrive(folder.File("MyOneDriveProject/a.dwg"), []));
        Assert.Equal("ONEDRIVE_WORKSPACE_BLOCKED", Assert.Throws<CadFault>(() => LocalWorkPolicy.RequireLocal(folder.File("OneDrive/a.dwg"))).Code);
    }
    [Fact]
    public void Csharp_permission_is_off_by_default_fails_closed_and_has_a_new_revision_after_each_user_change()
    {
        using var folder = new TempFolder(); string path = folder.File("policy.json");
        Assert.False(CSharpExecutionPolicy.Read(path).Allowed);
        CSharpExecutionPolicy.Store(true, path); var allowed = CSharpExecutionPolicy.Read(path); Assert.True(allowed.Allowed);
        CSharpExecutionPolicy.Store(false, path); Assert.False(CSharpExecutionPolicy.Read(path).Allowed);
        CSharpExecutionPolicy.Store(true, path); Assert.NotEqual(allowed.Revision, CSharpExecutionPolicy.Read(path).Revision);
        File.WriteAllText(path, "{damaged"); Assert.False(CSharpExecutionPolicy.Read(path).Allowed);
    }
}
