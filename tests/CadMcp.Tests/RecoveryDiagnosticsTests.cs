using System.Text.Json;
using CadMcp.Core;

namespace CadMcp.Tests;

public sealed class RecoveryDiagnosticsTests
{
    [Fact]
    public void Restart_keeps_project_evidence_without_leaking_other_drawings()
    {
        using var folder = new TempFolder();
        void Write(string file, object value) => File.WriteAllText(folder.File(file), JsonSerializer.Serialize(value, Wire.Json));
        Write("last-operation-old.json", new { session_id = "old", document_id = "old-doc", project_key = "project-A", phase = "running" });
        Write("last-operation-current.json", new { session_id = "current", document_id = "doc-A", project_key = "project-A", phase = "completed" });
        Write("last-operation-other.json", new { session_id = "current", document_id = "doc-B", project_key = "project-B" });
        File.WriteAllText(folder.File("last-operation-corrupt.json"), "{broken");
        var rows = RecoveryDiagnostics.Read(folder.Path, "current", "doc-A", "project-A");
        Assert.Equal(2, rows.Length);
        Assert.Contains(rows, r => r.Text("session_id") == "old" && r.Text("phase") == "running");
        Assert.DoesNotContain(rows, r => r.Text("document_id") == "doc-B");
    }
    [Fact]
    public void Legacy_checkpoints_are_matched_by_original_path_and_unsaved_documents_stay_isolated()
    {
        using var folder = new TempFolder(); var drawing = folder.File("Чертёж.dwg");
        File.WriteAllText(folder.File("last-operation.json"), JsonSerializer.Serialize(new { session_id = "old", document_id = "old-doc", checkpoint = new { original = drawing } }, Wire.Json));
        Assert.Single(RecoveryDiagnostics.Read(folder.Path, "new", "new-doc", ProjectIdentity.ForDrawing(drawing, "new-doc")));
        Assert.Empty(RecoveryDiagnostics.Read(folder.Path, "new", "new-doc", "unsaved-doc"));
        Assert.NotEqual(ProjectIdentity.ForDrawing("Drawing1.dwg", "A"), ProjectIdentity.ForDrawing("Drawing1.dwg", "B"));
    }
}
