using System.IO;
using System.Text.Json;
using System.Collections.Concurrent;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CadMcp.Core;
using App = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CadMcp.AutoCAD;

internal static class WorkSafety
{
    private static readonly object Sync = new();
    internal static readonly string Root = Wire.DataDirectory("recovery");
    private static readonly ConcurrentDictionary<string, string> Projects = new();
    internal static void RegisterDocument(string session, string document, string name) => Projects[session + ":" + document] = ProjectIdentity.ForDrawing(name, document);
    internal static bool Required(Request request, JsonElement[]? operations) => request.Operation == "cad_lisp" || operations is { Length: >= 25 }
        || operations?.Any(p => p.Text("op")?.StartsWith("map_",StringComparison.Ordinal)==true || p.Text("op") is "erase" or "solid_boolean" or "solid_shell" or "assembly_update"
            or "text_replace" or "layer_merge" or "explode" or "trim" or "join" or "block_import" || p.Text("action") == "delete") == true;
    internal static object Checkpoint(Document doc, string session, string document, string operation)
    {
        string directory = Path.Combine(Root, session, Portable.Hash(System.Text.Encoding.UTF8.GetBytes(document)));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N") + ".dwg");
        try
        {
            var info = WriteCopy(doc, path);
            using var content = File.OpenRead(path);
            var record = new { path, sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)), bytes = info.Length, operation_id = operation,
                original = doc.Name, created_at = DateTimeOffset.UtcNow, restore = "Open this copy separately; do not overwrite the working drawing" };
            File.WriteAllText(path + ".json", JsonSerializer.Serialize(record, Wire.Json));
            foreach (var old in Directory.EnumerateFiles(directory, "*.dwg").Select(p => new FileInfo(p)).OrderByDescending(p => p.CreationTimeUtc).Skip(10))
            { try { old.Delete(); File.Delete(old.FullName + ".json"); } catch (IOException) { } }
            return record;
        }
        catch (System.Exception e) { throw new CadFault("CHECKPOINT_FAILED", "Required DWG checkpoint failed; drawing was not changed: " + e.Message); }
    }
    /// <summary>Writes a DWG copy of the drawing without touching the working file or the document's name.</summary>
    internal static FileInfo WriteCopy(Document doc, string path)
    {
        // Wblock() clones the whole database, including layouts. Never SaveAs the live database.
        // Forced copy: a fast wblock could otherwise move objects out of the live drawing, and disposing the copy would free them.
        doc.Database.ForceWblockDatabaseCopy();
        using (var copy = doc.Database.Wblock()) copy.SaveAs(path, DwgVersion.Current);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0) throw new IOException("Empty DWG copy " + path);
        return info;
    }
    // The record this process wrote last, so per-action progress does not re-read and parse the file.
    private static Dictionary<string, JsonElement>? last;
    private static string? lastSession;
    internal static void Start(string session, Request request, object? checkpoint)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(Path.Combine(Root, session));
            var record = Wire.Element(new { session_id = session, document_id = request.DocumentId, operation_id = request.Data.Text("operation_id"),
                project_key = Projects.GetValueOrDefault(session + ":" + request.DocumentId),
                operation = request.Operation, owner_id = request.OwnerId, started_at = DateTimeOffset.UtcNow,
                plugin_version = typeof(WorkSafety).Assembly.GetName().Version?.ToString(), process_id = Environment.ProcessId, checkpoint, phase = "accepted" });
            Save(session, record.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()), durable: true);
        }
    }
    /// <summary>
    /// Record progress. Intermediate phases are written without forcing them to disk: they survive a CAD crash,
    /// and only an operating system failure could lose them. The final phase is flushed.
    /// </summary>
    internal static void Phase(string session, string operation, string phase, bool final = false)
    {
        try { lock (Sync)
        {
            // The record in memory may belong to another drawing (a LISP job waits in one DWG while another is edited);
            // each drawing has its own file, so look the operation up there.
            var data = lastSession == session && last?.GetValueOrDefault("operation_id").ValueKind == JsonValueKind.String
                && last["operation_id"].GetString() == operation ? last : null;
            if (data is null)
            {
                var file = Directory.Exists(Path.Combine(Root, session)) ? Directory.EnumerateFiles(Path.Combine(Root, session), "last-operation*.json")
                    .FirstOrDefault(p => { try { return JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(p), Wire.Json).Text("operation_id") == operation; } catch { return false; } }) : null;
                if (file is null) return;
                data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(file), Wire.Json)!;
            }
            if (data.GetValueOrDefault("operation_id").ValueKind != JsonValueKind.String || data["operation_id"].GetString() != operation) return;
            data["phase"] = Wire.Element(phase); data["last_signal_at"] = Wire.Element(DateTimeOffset.UtcNow);
            Save(session, data, final);
        } } catch(System.Exception e) when(e is IOException or UnauthorizedAccessException or JsonException){System.Diagnostics.Trace.WriteLine("Operation diagnostic: "+e.Message);}
    }
    private static void Save(string session, Dictionary<string, JsonElement> value, bool durable)
    {
        string path = Path.Combine(Root, session, "last-operation-" + Portable.Hash(System.Text.Encoding.UTF8.GetBytes(value["document_id"].GetString()!)) + ".json");
        using (var stream = new FileStream(path + ".tmp", FileMode.Create)) { JsonSerializer.Serialize(stream, value, Wire.Json); stream.Flush(durable); }
        Portable.ReplaceFile(path + ".tmp", path);
        last = value; lastSession = session;
    }
    internal static object Diagnostics(string session,string? document=null)
    {
        lock (Sync)
            return new { session_id = session, directory = Root, last_operations = RecoveryDiagnostics.Read(Root, session, document, document is null ? null : Projects.GetValueOrDefault(session + ":" + document)),
                autosave = AutoSaver.Describe(document),
                interpretation = "Non-terminal phase from a previous session means interrupted/unknown, never proof of rollback. Check checkpoint and receipt before retrying." };
    }
}

/// <summary>cad_save: the working drawing saved in place or under a new name, or a copy that leaves it untouched.</summary>
internal static class DrawingSaving
{
    internal static object Execute(Documents documents, Document doc, DocumentState state, string mode, string? path)
    {
        string previous = doc.Name;
        long revision = state.Revision;
        var started = DateTimeOffset.UtcNow;
        string target;
        switch (mode)
        {
            case "save":
                if (InPlaceBlocker(doc) is { } blocker) throw blocker;
                target = doc.Name;
                using (documents.ReadScope(doc)) SaveDocument(doc, null);
                break;
            case "save_as":
                if (doc.IsReadOnly) throw new CadFault("READ_ONLY_DRAWING", "The drawing is open read-only and cannot change its file; use mode copy");
                target = NewFile(doc, path);
                using (documents.ReadScope(doc)) SaveDocument(doc, target);
                break;
            case "copy":
                target = NewFile(doc, path);
                using (documents.ReadScope(doc)) WorkSafety.WriteCopy(doc, target);
                break;
            default: throw new CadFault("INVALID_SAVE_MODE", "mode is save, save_as or copy");
        }
        var file = new FileInfo(target);
        // AutoCAD reports some save failures only by leaving the file as it was.
        if (!file.Exists || file.Length == 0) throw new CadFault("SAVE_NOT_CONFIRMED", "No DWG was written to " + target);
        if (file.LastWriteTimeUtc < started.UtcDateTime.AddSeconds(-2)) throw new CadFault("SAVE_NOT_CONFIRMED", "The file was not rewritten: " + target);
        string? header = Header(target);
        if (mode != "copy") { state.AutoSavedRevision = revision; state.AutoSaveAttempt = DateTimeOffset.UtcNow; }
        using var content = File.OpenRead(target);
        return new { mode, path = target, previous_path = mode == "save_as" ? previous : null, document_name = doc.Name, saved_revision = revision,
            bytes = file.Length, sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)), last_write_utc = (DateTimeOffset)file.LastWriteTimeUtc,
            dwg_version = header, warning = header is null ? "The file does not start with a DWG header; AutoCAD's default save format may be DXF" : null,
            working_drawing = mode == "copy" ? "unchanged; the copy is a separate file" : "saved to disk" };
    }

    /// <summary>Why the drawing cannot be saved to its own file, or null.</summary>
    internal static CadFault? InPlaceBlocker(Document doc)
    {
        if (doc.IsReadOnly) return new("READ_ONLY_DRAWING", "The drawing is open read-only; use mode save_as with a new path, or copy");
        if (!doc.IsNamedDrawing || !Path.IsPathFullyQualified(doc.Name))
            return new("SAVE_AS_REQUIRED", "The drawing has never been saved; use mode save_as with an absolute .dwg path");
        try { if (File.Exists(doc.Name) && File.GetAttributes(doc.Name).HasFlag(FileAttributes.ReadOnly)) return new("READ_ONLY_FILE", "The DWG file is read-only on disk; use mode save_as or copy"); }
        catch (System.Exception e) when (e is IOException or UnauthorizedAccessException) { return new("FILE_UNAVAILABLE", e.Message); }
        return null;
    }

    private static string NewFile(Document doc, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new CadFault("INVALID_PATH", "Use an absolute path ending in .dwg");
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(path), ".dwg", StringComparison.OrdinalIgnoreCase)) throw new CadFault("INVALID_PATH", "The file extension must be .dwg; use cad_export for DXF or PDF");
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new CadFault("INVALID_PATH", "The folder does not exist: " + Path.GetDirectoryName(path));
        if (File.Exists(path) || string.Equals(path, doc.Name, StringComparison.OrdinalIgnoreCase))
            throw new CadFault("OUTPUT_EXISTS", "Choose a new file name; existing files are never overwritten (mode save writes the drawing's own file)");
        return path;
    }

    /// <summary>
    /// Saves through AutoCAD's ActiveX document, which behaves like QSAVE/SAVEAS: the drawing keeps its .bak, DBMOD is
    /// cleared and SAVEAS renames the document. Core Console has no ActiveX document and saves the database instead.
    /// </summary>
    private static void SaveDocument(Document doc, string? newPath)
    {
#if !CORE_CONSOLE
        object? document = null;
        try { document = Autodesk.AutoCAD.ApplicationServices.DocumentExtension.GetAcadDocument(doc); }
        catch (Autodesk.AutoCAD.Runtime.Exception) { }
        if (document is not null)
        {
            try
            {
                if (newPath is null) document.GetType().InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, document, null);
                else document.GetType().InvokeMember("SaveAs", System.Reflection.BindingFlags.InvokeMethod, null, document, [newPath]);
            }
            catch (System.Reflection.TargetInvocationException error) when (error.InnerException is not null)
            { throw new CadFault("SAVE_FAILED", "AutoCAD did not save the drawing: " + error.InnerException.Message); }
            return;
        }
        if (newPath is not null) throw new CadFault("SAVE_UNAVAILABLE", "This AutoCAD session cannot rename the drawing; use mode copy");
#endif
        doc.Database.SaveAs(newPath ?? doc.Name, true, DwgVersion.Current, doc.Database.SecurityParameters);
    }

    /// <summary>The DWG format code (AC1032 for the 2018 format), or null when the file is not a DWG.</summary>
    internal static string? Header(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var bytes = new byte[6];
            return stream.Read(bytes, 0, 6) == 6 && bytes[0] == 'A' && bytes[1] == 'C' ? System.Text.Encoding.ASCII.GetString(bytes) : null;
        }
        catch (System.Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// Periodic protection of unsaved work, run from Idle when nothing is queued: a rotating recovery copy (default) or,
/// when the user chose it with CADMCPAUTOSAVE, a save of the active drawing to its own file. One drawing per Idle.
/// </summary>
internal sealed class AutoSaver(Documents documents)
{
    internal sealed record Status(string DocumentId, string Name, string Mode, DateTimeOffset At, string? Path, long Revision, string? Error);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Status> Statuses = new();
    private AutoSaveSettings settings = AutoSaveSettings.Default;
    private DateTimeOffset settingsRead = DateTimeOffset.MinValue;
    /// <summary>The effective settings, read from disk at most every 5 seconds; Idle runs many times a second.</summary>
    internal static AutoSaveSettings Current { get; private set; } = AutoSaveSettings.Default;

    public void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - settingsRead > TimeSpan.FromSeconds(5)) { settings = Current = AutoSaveSettings.Effective(); settingsRead = now; }
        if (settings.Mode == AutoSaveMode.Off) return;
        foreach (var (doc, state) in documents.Open())
        {
            if (doc.IsDisposed || !AutoSaveSettings.Due(state.Revision, state.AutoSavedRevision, state.AutoSaveAttempt, now, settings.Interval)) continue;
            Run(doc, state, now);
            return;
        }
    }

    private void Run(Document doc, DocumentState state, DateTimeOffset now)
    {
        state.AutoSaveAttempt = now;
        long revision = state.Revision;
        bool save = settings.Mode == AutoSaveMode.Save && ReferenceEquals(doc, App.DocumentManager.MdiActiveDocument) && DrawingSaving.InPlaceBlocker(doc) is null;
        string? path = null;
        try
        {
            using var locked = doc.LockDocument();
            if (save)
            {
                var result = Wire.Element(DrawingSaving.Execute(documents, doc, state, "save", null));
                path = result.Text("path");
            }
            else
            {
                string folder = RecoveryCopies.Folder(WorkSafety.Root, ProjectIdentity.ForDrawing(doc.Name, state.Id));
                Directory.CreateDirectory(folder);
                path = Path.Combine(folder, RecoveryCopies.FileName(doc.Name, now));
                FileInfo info;
                using (documents.ReadScope(doc)) info = WorkSafety.WriteCopy(doc, path);
                File.WriteAllText(path + ".json", JsonSerializer.Serialize(new { path, original = doc.Name, revision, bytes = info.Length, created_at = now,
                    kind = "autosave_recovery_copy", restore = "Open this copy separately; do not overwrite the working drawing" }, Wire.Json));
                RecoveryCopies.Prune(folder);
            }
            state.AutoSavedRevision = revision;
            Statuses[state.Id] = new(state.Id, doc.Name, save ? "save" : "copy", now, path, revision, null);
        }
        catch (System.Exception error)
        {
            // Retried after the next interval; a failed autosave must never disturb the user's work.
            Statuses[state.Id] = new(state.Id, doc.Name, save ? "save" : "copy", now, path, revision, error.Message);
            System.Diagnostics.Trace.WriteLine("CAD MCP autosave: " + error);
        }
    }

    internal static void Forget(string documentId) => Statuses.TryRemove(documentId, out _);

    /// <summary>Settings and the last autosave of one drawing or of all; safe on any thread.</summary>
    internal static object Describe(string? documentId) => new
    {
        mode = AutoSaveSettings.Name(Current.Mode), interval_minutes = Current.Minutes, owner = "user (AutoCAD command CADMCPAUTOSAVE)",
        recovery_folder = Path.Combine(WorkSafety.Root, "autosave"),
        last = documentId is null ? Statuses.Values.OrderByDescending(s => s.At).Take(10).ToArray() : Statuses.TryGetValue(documentId, out var status) ? [status] : Array.Empty<Status>()
    };

    /// <summary>The summary a document state reports, matched by drawing name.</summary>
    internal static object Summary(Document doc) => new
    {
        mode = AutoSaveSettings.Name(Current.Mode), interval_minutes = Current.Minutes,
        last = Statuses.Values.Where(s => string.Equals(s.Name, doc.Name, StringComparison.OrdinalIgnoreCase)).OrderByDescending(s => s.At).FirstOrDefault()
    };
}
