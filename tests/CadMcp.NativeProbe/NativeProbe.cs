using System.Text.Json;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CadMcp.Core;
using CadMcp.AutoCAD;
using App = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CadMcp.NativeProbe.NativeProbe))]
namespace CadMcp.NativeProbe;

public static class NativeProbe
{
    private static Dispatcher? dispatcher;
    [LispFunction("CADMCPBEGIN")]
    public static string? Begin(ResultBuffer args) => dispatcher?.BeginLisp((string)args.AsArray()[0].Value);
    [LispFunction("CADMCPFINISH")]
    public static int Finish(ResultBuffer args)
    { var v = args.AsArray(); dispatcher?.FinishLisp((string)v[0].Value, Convert.ToInt32(v[1].Value) == 1, (string)v[2].Value); return 0; }
    [CommandMethod("CADMCPPROBE", CommandFlags.Session)]
    public static async void Run()
    {
        var output = Environment.GetEnvironmentVariable("CADMCP_PROBE_OUTPUT");
        if (string.IsNullOrWhiteSpace(output) || !File.Exists(output + ".pid") || File.ReadAllText(output + ".pid").Trim() != Environment.ProcessId.ToString()) return;
        try { await RunProbe(output); }
        catch (System.Exception e) { File.WriteAllText(output, JsonSerializer.Serialize(new { passed = 0, failure = e.ToString() })); }
    }
    private static async Task RunProbe(string output)
    {
        var doc = Autodesk.AutoCAD.ApplicationServices.DocumentCollectionExtension.Add(App.DocumentManager, "acadiso.dwt");
        App.DocumentManager.MdiActiveDocument = doc;
        using var documents = new Documents(); dispatcher = new(documents); dispatcher.Start();
        var checks = new List<string>();
        string? failure = null, document = null; long revision = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        void Assert(bool condition, string message)
        { if (!condition) throw new System.Exception(message); checks.Add(message); File.WriteAllText(output + ".progress", JsonSerializer.Serialize(checks)); }
        async Task<Response> Call(string operation, object data, long? expected = null)
        {
            var response = await dispatcher!.Enqueue(new(Guid.NewGuid().ToString("N"), operation, documents.SessionId, document,
                operation == "cad_context" ? null : expected ?? revision, Wire.Element(data), DateTimeOffset.UtcNow.AddSeconds(20)), timeout.Token);
            if (response.Revision is { } current) revision = current;
            return response;
        }
        JsonElement Data(Response r) { if (r.Error is not null) throw new System.Exception(r.Error.Code + ": " + r.Error.Message); return Wire.Element(r.Data!); }
        Task OnIdle(Action action)
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler? handler = null;
            handler = (_, _) => { App.Idle -= handler; try { action(); source.TrySetResult(); } catch (System.Exception error) { source.TrySetException(error); } };
            App.Idle += handler; return source.Task.WaitAsync(timeout.Token);
        }
        try
        {
            var context = await Call("cad_context", new { }); document = context.DocumentId; revision = context.Revision!.Value;
            var edits = """
                [{"op":"layer","name":"CADMCP_TEST","color_index":3},
                 {"op":"line","id":"line","start":[0,0,0],"end":[100,0,0],"layer":"CADMCP_TEST"},
                 {"op":"circle","id":"circle","center":[50,50,0],"radius":10},
                 {"op":"polyline","id":"poly","points":[[0,100],[50,100],[50,150]]},
                 {"op":"rectangle","id":"rect","first":[100,100],"second":[150,150]},
                 {"op":"text","position":[0,50],"text":"Сеть ✓","height":2.5},
                 {"op":"mtext","position":[0,60],"text":"Колодец","height":2.5,"width":30},
                 {"op":"arc","center":[100,50],"radius":20,"start_angle_deg":0,"end_angle_deg":90},
                 {"op":"dimension_aligned","first":[0,0],"second":[100,0],"position":[0,-10]},
                 {"op":"hatch","boundaries":["rect"]},
                 {"op":"box","center":[200,0,0],"length":10,"width":20,"height":30},
                 {"op":"cylinder","center":[250,0,0],"radius":10,"height":20}]
                """;
            var original = new Request("seed", "cad_edit", documents.SessionId, document, revision, Wire.Element(new { operation_id = "seed", operations_json = edits }), DateTimeOffset.UtcNow.AddSeconds(20));
            var created = await dispatcher.Enqueue(original, timeout.Token); revision = created.Revision!.Value;
            var createdData = Data(created).GetProperty("result");
            Assert(createdData.GetProperty("entities").GetArrayLength() == 11, "Created 11 real entities including dimensions/hatch/solids");
            var entities = createdData.GetProperty("entities").EnumerateArray().ToArray();
            Assert(entities.Any(e => e.Text("text") == "Сеть ✓"), "Unicode text retained in DWG");
            var line = entities.First(e => e.Text("type") == "Line").Text("handle")!;
            var circle = entities.First(e => e.Text("type") == "Circle").Text("handle")!;
            var native = Data(await Call("cad_edit", new { operation_id = "native_extended", operations_json = JsonSerializer.Serialize(new object[] {
                new { op = "point", position = new[] { 310, 10, 0 } },
                new { op = "ellipse", center = new[] { 330, 10, 0 }, major_axis = new[] { 12, 0, 0 }, radius_ratio = 0.5 },
                new { op = "block_define", name = "CADMCP_NATIVE_BLOCK", base_point = new[] { 0, 0, 0 }, handles = new[] { line, circle } },
                new { op = "layout_create", name = "CADMCP_A3" } }) }));
            Assert(native.GetProperty("result").GetProperty("entities").EnumerateArray().Any(e => e.Text("type") == "Ellipse"), "Native C# ellipse read back");
            var nativeCatalog = Data(await Call("cad_catalog", new { }));
            Assert(nativeCatalog.GetProperty("layouts").EnumerateArray().Any(x => x.Text("name") == "CADMCP_A3") &&
                nativeCatalog.GetProperty("blocks").EnumerateArray().Any(x => x.Text("name") == "CADMCP_NATIVE_BLOCK"), "Native C# block definition and layout created");
            var exported = Data(await Call("cad_export", new { operation_id = "native_dxf", format = "dxf", path = output + ".dxf" }));
            Assert(exported.GetProperty("result").GetProperty("bytes").GetInt64() > 0 && File.Exists(output + ".dxf"), "Native C# DXF export verified");
            var replay = Data(await dispatcher.Enqueue(original with { RequestId = "retry" }, timeout.Token));
            Assert(replay.GetProperty("replayed").GetBoolean(), "Exact retry replayed without duplicate entities");
            var changed = Data(await Call("cad_edit", new { operation_id = "move", operations_json = JsonSerializer.Serialize(new object[] {
                new { op = "move", handle = line, displacement = new[] { 10, 20, 0 } },
                new { op = "copy", id = "duplicate", handle = line, displacement = new[] { 0, 10, 0 } },
                new { op = "rotate", target = "duplicate", center = new[] { 0, 0, 0 }, angle_deg = 90 },
                new { op = "set", handle = line, color_index = 1 },
                new { op = "erase", target = "duplicate" } }) }));
            var lineData = Data(await Call("cad_entity_get", new { handle = line }));
            Assert(lineData.GetProperty("start")[0].GetDouble() == 10 && lineData.GetProperty("start")[1].GetDouble() == 20 && lineData.GetProperty("color_index").GetInt32() == 1, "Move and property edit verified by native readback");
            Assert(changed.GetProperty("result").GetProperty("entities").EnumerateArray().Any(e => e.TryGetProperty("erased", out var erased) && erased.GetBoolean()), "Copy/rotate/delete committed");
            var before = Data(await Call("cad_snapshot", new { limit = 100 }));
            var failed = await Call("cad_edit", new { operation_id = "rollback", operations_json = "[{\"op\":\"line\",\"start\":[777,777],\"end\":[888,888]},{\"op\":\"line\",\"start\":[0,0],\"end\":[1,1],\"layer\":\"MISSING_LAYER\"}]" });
            Assert(failed.Error?.Code == "LAYER_NOT_FOUND", "Fault in second step reported");
            var after = Data(await Call("cad_snapshot", new { limit = 100 }));
            Assert(after.GetProperty("captured").GetInt32() == before.GetProperty("captured").GetInt32(), "Failed native batch actually rolled back first step");
            var second = Data(await Call("cad_snapshot", new { limit = 100 }));
            Assert(second.GetProperty("cached").GetBoolean(), "Snapshot reused at unchanged revision");
            var options = JsonSerializer.Serialize(new { scope = "all", type = "Line", limit = 10 });
            Data(await Call("cad_search", new { options_json = options }));
            Assert(Data(await Call("cad_search", new { options_json = options })).GetProperty("cached").GetBoolean(), "Targeted search cached at unchanged revision");
            int beforeUndo = Data(await Call("cad_snapshot", new { limit = 100 })).GetProperty("captured").GetInt32();
            var undo = Data(await Call("cad_edit", new { operation_id = "undo_test", operations_json = "[{\"op\":\"line\",\"start\":[600,600],\"end\":[610,600]},{\"op\":\"line\",\"start\":[600,610],\"end\":[610,610]}]" }));
            Assert(undo.GetProperty("result").Text("undo") == "single_undo_group", "Actual AutoCAD COM undo group opened");
            Assert(!Data(await Call("cad_search", new { options_json = options })).GetProperty("cached").GetBoolean(), "Committed edit invalidates search cache");
            await OnIdle(() => doc.SendStringToExecute("_.UNDO\n1\n", false, false, false));
            await Task.Delay(500, timeout.Token);
            Data(await Call("cad_context", new { })); // Undo changes revision just like any manual edit.
            Assert(Data(await Call("cad_snapshot", new { limit = 100 })).GetProperty("captured").GetInt32() == beforeUndo, "One Undo removes both entities from the batch");
            var stale = await Call("cad_edit", new { operation_id = "stale", operations_json = "[{\"op\":\"erase\",\"handle\":\"" + line + "\"}]" }, revision - 1);
            Assert(stale.Error?.Code == "REVISION_CONFLICT", "Stale native mutation rejected");
            Data(await Call("cad_lisp", new { operation_id = "lisp_math", code = "(+ 1 2)" }));
            for (int i = 0; i < 100; i++)
            {
                await Task.Delay(100, timeout.Token);
                var state = Data(await Call("cad_operation_status", new { operation_id = "lisp_math" }));
                if (state.Text("state") is not ("completed" or "failed")) continue;
                var result = state.GetProperty("result");
                Assert(!result.TryGetProperty("error", out _), "Real AutoLISP wrapper completed without errors");
                Assert(result.GetProperty("data").Text("return_value") == "3", "AutoLISP result returned through managed callback");
                break;
            }
            Assert(Data(await Call("cad_operation_status", new { operation_id = "lisp_math" })).Text("state") == "completed", "LISP journal reached terminal state");
            async Task<JsonElement> WaitForLisp(string id)
            {
                for (int i = 0; i < 100; i++)
                {
                    await Task.Delay(100, timeout.Token);
                    var operation = Data(await Call("cad_operation_status", new { operation_id = id }));
                    if (operation.Text("state") is "completed" or "failed") return operation;
                }
                throw new System.Exception("AutoLISP did not reach terminal state: " + id);
            }
            Data(await Call("cad_lisp", new { operation_id = "lisp_error", code = "(/ 1 0)" }));
            var lispError = await WaitForLisp("lisp_error");
            Assert(lispError.Text("state") == "failed" && lispError.GetProperty("result").GetProperty("error").Text("code") == "LISP_FAILED", "AutoLISP evaluator error persisted as failed");
            bool cancelSent = false;
            CommandEventHandler cancelLine = (_, e) =>
            {
                if (e.GlobalCommandName != "LINE") return;
                cancelSent = true;
                // Escape characters target only this probe's own scratch document.
                doc.SendStringToExecute("\u0003\u0003", false, false, false);
            };
            await OnIdle(() => doc.CommandWillStart += cancelLine);
            try
            {
                Data(await Call("cad_lisp", new { operation_id = "lisp_cancel", code = "(command \"_.LINE\" '(900 900 0) pause)" }));
                var cancelled = await WaitForLisp("lisp_cancel");
                Assert(cancelSent && cancelled.Text("state") == "failed", "Esc interrupts real AutoLISP and persists failed state");
                Assert(cancelled.GetProperty("result").GetProperty("data").GetProperty("partial_changes_possible").GetBoolean(), "Interrupted AutoLISP reports possible partial changes");
            }
            finally { await OnIdle(() => doc.CommandWillStart -= cancelLine); }
            Data(await Call("cad_context", new { }));
            Document? other = null;
            await OnIdle(() => { other = Autodesk.AutoCAD.ApplicationServices.DocumentCollectionExtension.Add(App.DocumentManager, "acadiso.dwt"); App.DocumentManager.MdiActiveDocument = other; });
            try
            {
                var mismatch = await Call("cad_edit", new { operation_id = "wrong_doc", operations_json = "[{\"op\":\"line\",\"start\":[0,0],\"end\":[1,1]}]" });
                Assert(mismatch.Error?.Code == "DOCUMENT_MISMATCH", "Switching drawing blocks edits to the wrong active DWG");
            }
            finally { await OnIdle(() => { App.DocumentManager.MdiActiveDocument = doc; other!.CloseAndDiscard(); }); }
        }
        catch (System.Exception e) { failure = e.ToString(); }
        finally
        {
            App.DocumentManager.ExecuteInApplicationContext(_ =>
            {
                dispatcher.Dispose(); dispatcher = null;
                try { if (!doc.IsDisposed) doc.CloseAndDiscard(); } catch (System.Exception e) { failure ??= "Scratch cleanup: " + e; }
                File.WriteAllText(output, JsonSerializer.Serialize(new { product = Environment.GetEnvironmentVariable("CADMCP_PROBE_PRODUCT"), acad_version = Convert.ToString(App.GetSystemVariable("ACADVER")), passed = checks.Count, checks, failure }, new JsonSerializerOptions { WriteIndented = true }));
                if (File.ReadAllText(output + ".pid").Trim() == Environment.ProcessId.ToString())
                    App.DocumentManager.MdiActiveDocument?.SendStringToExecute("_.QUIT\n_Yes\n", false, false, false);
            }, null);
        }
    }
}
