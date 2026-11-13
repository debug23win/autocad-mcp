using System.Reflection;
using System.Runtime.Loader;
using CadMcp.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using App = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CadMcp.AutoCAD;

/// <summary>Runs only in the newly launched acad.exe. Never advertises a broker worker or starts the chat.</summary>
internal static class IsolatedCSharpWorker
{
    private static string? requestPath;
    private static bool scheduled;
    internal static bool TryStart()
    {
        string? path = Environment.GetEnvironmentVariable(IsolatedCSharpJob.EnvironmentKey);
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!scheduled) { scheduled = true; requestPath = path; App.Idle += Run; }
        return true;
    }
    private static void Run(object? sender, EventArgs args)
    {
        if (!App.DocumentManager.IsApplicationContext || App.DocumentManager.MdiActiveDocument?.Editor.IsQuiescent == false) return;
        App.Idle -= Run;
        IsolatedCSharpJob? job = null;
        Document? document = null;
        AssemblyLoadContext? load = null;
        bool duplicate = false;
        try
        {
            job = IsolatedCSharpJob.Load(requestPath!);
            // An autoload plus NETLOAD, or another adapter, must not run the script twice.
            try { using var claim = new FileStream(job.FilePath("claimed"), FileMode.CreateNew); }
            catch (IOException) when (File.Exists(job.FilePath("claimed"))) { duplicate = true; return; }
            using (var parent = System.Diagnostics.Process.GetProcessById(job.Request.ParentProcessId))
                if (parent.HasExited) throw new InvalidOperationException("Parent AutoCAD is no longer running");
            CSharpExecutionPolicy.Read().RequireAllowed();
            job.Phase("opening_copy");
            using var tracking = new Documents();
            document = App.DocumentManager.Open(job.FilePath("input.dwg"), false);
            App.DocumentManager.MdiActiveDocument = document;
            var tracked = tracking.Register(document); long beforeRevision = tracked.Revision;
            using var timeout = new CancellationTokenSource(job.Request.Deadline - DateTimeOffset.UtcNow);
            System.Text.Json.JsonElement value;
            using (document.LockDocument())
            {
                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    load = new AssemblyLoadContext("cadmcp-isolated-" + job.Request.RunId, isCollectible: true);
                    using var stream = File.OpenRead(job.FilePath("script.dll"));
                    var assembly = load.LoadFromStream(stream);
                    var method = assembly.GetType("CadMcpScript")?.GetMethod("Run", BindingFlags.Public | BindingFlags.Static,
                        null, [typeof(Database), typeof(Transaction), typeof(CancellationToken)], null)
                        ?? throw new CadFault("CSHARP_ENTRYPOINT", "Expected CadMcpScript.Run(Database, Transaction, CancellationToken)");
                    if (method.ReturnType != typeof(object)) throw new CadFault("CSHARP_ENTRYPOINT", "Run must return object synchronously");
                    job.Phase("running_script");
                    object? result;
                    try { result = method.Invoke(null, [document.Database, transaction, timeout.Token]); }
                    catch (TargetInvocationException error) { throw error.InnerException ?? error; }
                    timeout.Token.ThrowIfCancellationRequested();
                    if (result is Task or ValueTask || result is not null && result.GetType().IsGenericType && result.GetType().GetGenericTypeDefinition() == typeof(ValueTask<>))
                        throw new CadFault("CSHARP_ASYNC_RESULT", "Return a completed DTO");
                    value = Wire.Element(result ?? new { });
                    if (ReadPayloadBudget.Bytes(value) > ReadPayloadBudget.EntityBytes) throw new CadFault("CSHARP_RESULT_TOO_LARGE", "Return a bounded DTO/handles");
                    transaction.Commit(); job.Phase("committed_in_child");
                }
                timeout.Token.ThrowIfCancellationRequested();
                job.Phase("saving_output");
                document.Database.SaveAs(job.FilePath("result.dwg"), DwgVersion.Current);
            }
            var changes = tracked.ChangesSince(beforeRevision).Where(c => c.Entity).Select(c => c.Handle.ToString("X")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            bool complete = tracked.DroppedThrough <= beforeRevision, bounded = changes.Sum(h => h.Length + 3) <= 192 * 1024;
            document.CloseAndDiscard(); document = null;
            job.Phase("verifying_saved_output");
            using (var saved = new Database(false, true))
            {
                saved.ReadDwgFile(job.FilePath("result.dwg"), FileOpenMode.OpenForReadAndAllShare, true, null);
                saved.CloseInput(true);
            }
            timeout.Token.ThrowIfCancellationRequested();
            var info = new FileInfo(job.FilePath("result.dwg"));
            if (info.Length == 0) throw new InvalidDataException("Empty output DWG");
            job.Write("result.json", new IsolatedCSharpReceipt(job.Request.RunId, job.Request.Nonce, job.Request.OperationId, Environment.ProcessId,
                "completed", job.Request.SourceSha256, job.Request.InputSha256, IsolatedCSharpJob.HashFile(info.FullName), info.Length, value,
                ChangedHandles: complete && bounded ? changes : null, ChangedEntityCount: complete ? changes.Length : null, ChangedHandlesOmitted: !complete || !bounded));
            job.Phase("completed");
        }
        catch (System.Exception error)
        {
            if (job is not null)
                try
                {
                    job.Phase("failed");
                    job.Write("result.json", new IsolatedCSharpReceipt(job.Request.RunId, job.Request.Nonce, job.Request.OperationId, Environment.ProcessId,
                        "failed", job.Request.SourceSha256, job.Request.InputSha256, Error: new(error is CadFault fault ? fault.Code : "CSHARP_EXECUTION_FAILED", error.Message)));
                }
                catch (System.Exception diagnosticError) { System.Diagnostics.Trace.WriteLine(diagnosticError); }
        }
        finally
        {
            try { document?.CloseAndDiscard(); } catch (System.Exception error) { System.Diagnostics.Trace.WriteLine(error); }
            try { load?.Unload(); } catch (System.Exception error) { System.Diagnostics.Trace.WriteLine(error); }
            // If AutoCAD shows a startup/quit dialog, the parent's deadline kills only this owned child.
            if (!duplicate) try { App.Quit(); } catch (System.Exception error) { System.Diagnostics.Trace.WriteLine(error); }
        }
    }
}
