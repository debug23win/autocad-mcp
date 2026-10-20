using CadMcp.Core;

namespace CadMcp.Tests;

[Collection("Environment")]
public sealed class AutoSaveTests
{
    [Fact]
    public void Settings_default_to_a_recovery_copy_persist_and_yield_to_the_environment()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cadmcp-autosave-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "settings.json");
        var previous = Environment.GetEnvironmentVariable("CAD_MCP_AUTOSAVE");
        try
        {
            Environment.SetEnvironmentVariable("CAD_MCP_AUTOSAVE", null);
            Assert.Equal(new AutoSaveSettings(AutoSaveMode.Copy, 10), AutoSaveSettings.Effective(path));
            // Storing autosave keeps the AutoLISP policy stored in the same file, and the other way round.
            CadSettings.StoreLispPolicy(LispPolicyMode.Deny, path);
            AutoSaveSettings.Store(new(AutoSaveMode.Save, 5), path);
            Assert.Equal(new AutoSaveSettings(AutoSaveMode.Save, 5), AutoSaveSettings.Stored(path));
            Assert.Equal(LispPolicyMode.Deny, CadSettings.StoredLispPolicy(path));
            CadSettings.StoreLispPolicy(LispPolicyMode.Ask, path);
            Assert.Equal(new AutoSaveSettings(AutoSaveMode.Save, 5), AutoSaveSettings.Stored(path));
            // A damaged or hand-edited file falls back to the copy, which never touches the user's DWG.
            File.WriteAllText(path, "{ broken");
            Assert.Equal(AutoSaveSettings.Default, AutoSaveSettings.Stored(path));
            File.WriteAllText(path, "{\"autosave_mode\":\"always\",\"autosave_minutes\":5}");
            Assert.Equal(AutoSaveSettings.Default, AutoSaveSettings.Stored(path));
            File.WriteAllText(path, "{\"autosave_mode\":\"off\",\"autosave_minutes\":100000}");
            Assert.Equal(new AutoSaveSettings(AutoSaveMode.Off, AutoSaveSettings.MaxMinutes), AutoSaveSettings.Stored(path));
            Environment.SetEnvironmentVariable("CAD_MCP_AUTOSAVE", "save:3");
            Assert.Equal(new AutoSaveSettings(AutoSaveMode.Save, 3), AutoSaveSettings.Effective(path));
            Environment.SetEnvironmentVariable("CAD_MCP_AUTOSAVE", "save:0");
            Assert.Equal(AutoSaveMode.Off, AutoSaveSettings.Effective(path).Mode);
            Assert.Throws<ArgumentOutOfRangeException>(() => AutoSaveSettings.Store(new(AutoSaveMode.Copy, 0), path));
            foreach (var mode in Enum.GetValues<AutoSaveMode>()) Assert.Equal(mode, AutoSaveSettings.Parse(AutoSaveSettings.Name(mode)).Mode);
            Assert.Equal("INVALID_AUTOSAVE", Assert.Throws<CadFault>(() => AutoSaveSettings.Parse("hourly")).Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CAD_MCP_AUTOSAVE", previous);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void A_drawing_is_autosaved_only_after_a_change_and_once_per_interval()
    {
        var start = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var interval = TimeSpan.FromMinutes(10);
        Assert.False(AutoSaveSettings.Due(5, 5, start, start.AddHours(1), interval));
        Assert.False(AutoSaveSettings.Due(6, 5, start, start.AddMinutes(9), interval));
        Assert.True(AutoSaveSettings.Due(6, 5, start, start.AddMinutes(10), interval));
        // UNDO back to the saved state still differs in revision: a revision only grows, so the copy is made.
        Assert.True(AutoSaveSettings.Due(7, 5, start, start.AddMinutes(11), interval));
    }

    [Fact]
    public void Recovery_copies_rotate_per_project_and_keep_safe_names()
    {
        var root = Path.Combine(Path.GetTempPath(), "cadmcp-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var folder = RecoveryCopies.Folder(root, "project");
            Directory.CreateDirectory(folder);
            var time = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
            Assert.Equal("20261020-120000-000-План этажа.dwg", RecoveryCopies.FileName(@"C:\Проекты\План этажа.dwg", time));
            Assert.Equal("20261020-120000-000-drawing.dwg", RecoveryCopies.FileName("", time));
            Assert.DoesNotContain('"', RecoveryCopies.FileName("a\"b?.dwg", time));
            var names = Enumerable.Range(0, 5).Select(i => Path.Combine(folder, RecoveryCopies.FileName("Лист.dwg", time.AddMinutes(i)))).ToArray();
            foreach (var name in names) { File.WriteAllText(name, "AC1032"); File.WriteAllText(name + ".json", "{}"); }
            var removed = RecoveryCopies.Prune(folder);
            Assert.Equal(names.Take(2).Order(StringComparer.Ordinal), removed.Order(StringComparer.Ordinal));
            Assert.Equal(names.Skip(2).Order(StringComparer.Ordinal), Directory.EnumerateFiles(folder, "*.dwg").Order(StringComparer.Ordinal));
            Assert.Equal(RecoveryCopies.Keep, Directory.EnumerateFiles(folder, "*.json").Count());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
