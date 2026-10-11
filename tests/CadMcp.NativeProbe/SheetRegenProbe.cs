using System.Text.Json;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CadMcp.Core;
using CadMcp.AutoCAD;
using App = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CadMcp.NativeProbe.SheetRegenProbe))]
namespace CadMcp.NativeProbe;

/// <summary>
/// Reproduces a sheet batch (SPDS frame and title block, a table with formulas, notes) through the production path:
/// dispatcher, cad_edit_preview, cad_edit with its commit and regeneration, then garbage collection, regenerations and a
/// layout switch. Every step is appended to output.progress and flushed before it runs, so after a crash of AutoCAD the
/// last line names the step that crashed. Each variant runs in its own new drawing.
/// render_then_edit renders the sheet with cad_render, as the chat panel does after every turn, before the next edit;
/// frame_explode explodes the generated title block with its multiline attributes as text, and closing the drawing
/// then frees everything the explode created.
/// CADMCP_SHEET_VARIANTS limits the variants (comma list); CADMCP_SHEET_PLAN names a JSON file with an operations array,
/// or an operation receipt from %LOCALAPPDATA%\CadMcp\operations, to replay a real plan.
/// </summary>
public static class SheetRegenProbe
{
    private const string Layout = "CADMCP_A1";

    [CommandMethod("CADMCPSHEETREGEN", CommandFlags.Session)]
    public static async void Run()
    {
        var output = Environment.GetEnvironmentVariable("CADMCP_PROBE_OUTPUT");
        if (string.IsNullOrWhiteSpace(output) || !File.Exists(output + ".pid") || File.ReadAllText(output + ".pid").Trim() != Environment.ProcessId.ToString()) return;
        var completed = new List<string>();
        string? failure = null;
        void Log(string step)
        {
            // Flushed to disk before the step runs: a native crash leaves this line as the last one.
            using var file = new FileStream(output + ".progress", FileMode.Append, FileAccess.Write, FileShare.Read);
            var bytes = System.Text.Encoding.UTF8.GetBytes(DateTimeOffset.Now.ToString("HH:mm:ss.fff") + " " + step + Environment.NewLine);
            file.Write(bytes); file.Flush(true);
        }
        using var documents = new Documents();
        var dispatcher = new Dispatcher(documents); dispatcher.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        Task OnIdle(Action action)
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler? handler = null;
            handler = (_, _) => { App.Idle -= handler; try { action(); source.TrySetResult(); } catch (System.Exception error) { source.TrySetException(error); } };
            App.Idle += handler; return source.Task.WaitAsync(timeout.Token);
        }
        try
        {
            Log("start AutoCAD " + Convert.ToString(App.GetSystemVariable("ACADVER")) + " FIELDEVAL=" + Convert.ToString(App.GetSystemVariable("FIELDEVAL")));
            var variants = Variants();
            var only = Environment.GetEnvironmentVariable("CADMCP_SHEET_VARIANTS")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var (name, plan, preview, then) in variants)
            {
                if (only is { Length: > 0 } && !only.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                Document? doc = null;
                Log(name + ": new drawing");
                await OnIdle(() => { doc = DocumentCollectionExtension.Add(App.DocumentManager, "acadiso.dwt"); App.DocumentManager.MdiActiveDocument = doc; });
                try
                {
                    string? document = null; long revision = 0;
                    async Task<JsonElement> Call(string operation, object data, long? expected = null)
                    {
                        var response = await dispatcher.Enqueue(new(Guid.NewGuid().ToString("N"), operation, documents.SessionId, document,
                            operation == "cad_context" ? null : expected ?? revision, Wire.Element(data), DateTimeOffset.UtcNow.AddMinutes(5)), timeout.Token);
                        if (response.Revision is { } current) revision = current;
                        if (response.Error is { } error) throw new System.Exception(name + " " + operation + ": " + error.Code + ": " + error.Message);
                        return Wire.Element(response.Data!);
                    }
                    // The drawing and its revision come with the response, as for any request.
                    var context = await dispatcher.Enqueue(new(Guid.NewGuid().ToString("N"), "cad_context", documents.SessionId, null, null, Wire.Element(new { }),
                        DateTimeOffset.UtcNow.AddMinutes(1)), timeout.Token);
                    document = context.DocumentId ?? throw new System.Exception("No document id: " + context.Error?.Message);
                    revision = context.Revision!.Value;
                    // A layout switch or a preview outside the dispatcher can move the drawing's revision.
                    async Task Refresh()
                    {
                        var current = await dispatcher.Enqueue(new(Guid.NewGuid().ToString("N"), "cad_context", documents.SessionId, document, null, Wire.Element(new { }),
                            DateTimeOffset.UtcNow.AddMinutes(1)), timeout.Token);
                        revision = current.Revision ?? revision;
                    }
                    foreach (var layout in Layouts(plan))
                    {
                        Log(name + ": layout_create " + layout);
                        await Call("cad_edit", new { operation_id = Id(name, "layout"), operations_json = JsonSerializer.Serialize(new[] { new { op = "layout_create", name = layout } }) });
                    }
                    JsonElement edited;
                    if (preview)
                    {
                        Log(name + ": cad_edit_preview");
                        var previewed = await Call("cad_edit_preview", new { operations_json = plan });
                        Log(name + ": cad_edit with preview_hash (commit and display refresh inside the executor)");
                        edited = await Call("cad_edit", new { operation_id = Id(name, "edit"), operations_json = plan, preview_hash = previewed.Text("plan_hash") }, Data(previewed, "previewed_revision"));
                    }
                    else
                    {
                        Log(name + ": cad_edit (commit and display refresh inside the executor)");
                        edited = await Call("cad_edit", new { operation_id = Id(name, "edit"), operations_json = plan });
                    }
                    if (then == "render")
                    {
                        // The chat panel renders after every turn with a completed edit; the next turn edits again.
                        for (int frame = 1; frame <= 3; frame++)
                        {
                            Log(name + ": cad_render " + frame + " of layout " + Layout);
                            await Call("cad_render", new { layout_name = Layout, width = 640 + frame * 128, height = 480 + frame * 96 });
                        }
                        Log(name + ": garbage collection after the previews");
                        await OnIdle(() => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); });
                        Log(name + ": cad_edit after the previews (commit and display refresh inside the executor)");
                        await Refresh();
                        await Call("cad_edit", new { operation_id = Id(name, "after_render"), operations_json = JsonSerializer.Serialize(new[] { new { op = "mtext", layout = Layout,
                            position = new[] { 420, 60, 0 }, text = "Изменение после превью", height = 3.5, width = 120 } }) });
                    }
                    if (then == "explode")
                    {
                        // The frame's result names the title block reference.
                        var frameHandle = edited.GetProperty("result").GetProperty("results")[0].Text("handle") ?? throw new System.Exception("The frame returned no handle");
                        // Explode edits the current space only.
                        Log(name + ": switch to layout " + Layout + " for the explode");
                        await OnIdle(() => { using var locked = doc!.LockDocument(); LayoutManager.Current.CurrentLayout = Layout; });
                        await Refresh();
                        Log(name + ": explode title block " + frameHandle + " with its attributes as text");
                        await Call("cad_edit", new { operation_id = Id(name, "explode"), operations_json = JsonSerializer.Serialize(new[] { new { op = "explode", handle = frameHandle, attributes_as_text = true } }) });
                    }
                    Log(name + ": garbage collection with finalizers");
                    await OnIdle(() => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); });
                    Log(name + ": regeneration of the current space");
                    await OnIdle(() => { using var locked = doc!.LockDocument(); doc.Editor.Regen(); });
                    foreach (var layout in Layouts(plan).DefaultIfEmpty(Layout))
                    {
                        Log(name + ": switch to layout " + layout);
                        await OnIdle(() => { using var locked = doc!.LockDocument(); LayoutManager.Current.CurrentLayout = layout; });
                        Log(name + ": regeneration of layout " + layout);
                        await OnIdle(() => { using var locked = doc!.LockDocument(); doc.Editor.Regen(); });
                    }
                    Log(name + ": second garbage collection and regeneration");
                    await OnIdle(() => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); using var locked = doc!.LockDocument(); doc.Editor.Regen(); });
                    Log(name + ": back to model space");
                    await OnIdle(() => { using var locked = doc!.LockDocument(); LayoutManager.Current.CurrentLayout = "Model"; });
                    completed.Add(name);
                    Log(name + ": passed");
                }
                finally
                {
                    // Closing destroys the drawing's database: objects with two owners are freed twice here.
                    Log(name + ": close the drawing");
                    await OnIdle(() => { if (doc is { IsDisposed: false }) doc.CloseAndDiscard(); });
                }
            }
        }
        catch (System.Exception e) { failure = e.ToString(); Log("failure: " + e.Message); }
        finally
        {
            App.DocumentManager.ExecuteInApplicationContext(_ =>
            {
                dispatcher.Dispose();
                File.WriteAllText(output, JsonSerializer.Serialize(new { probe = "sheet_regeneration", acad_version = Convert.ToString(App.GetSystemVariable("ACADVER")), passed = completed.Count, checks = completed, failure },
                    new JsonSerializerOptions { WriteIndented = true }));
                if (File.ReadAllText(output + ".pid").Trim() == Environment.ProcessId.ToString())
                    App.DocumentManager.MdiActiveDocument?.SendStringToExecute("_.QUIT\n_Yes\n", false, false, false);
            }, null);
        }
    }

    private static long Data(JsonElement data, string name) => data.GetProperty(name).GetInt64();
    private static string Id(string variant, string step) => "sheet_" + variant + "_" + step + "_" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Layouts the plan draws on that it does not create itself.</summary>
    private static IEnumerable<string> Layouts(string plan)
    {
        using var parsed = JsonDocument.Parse(plan);
        var created = parsed.RootElement.EnumerateArray().Where(o => o.Text("op") == "layout_create").Select(o => o.Text("name")).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return parsed.RootElement.EnumerateArray().Select(o => o.Text("layout")).OfType<string>().Where(l => !created.Contains(l)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static List<(string Name, string Plan, bool Preview, string? Then)> Variants()
    {
        var frame = new object[] { new { op = "spds_sheet", layout = Layout, format = "A1", designation = "TEST-КЖ", project = "Проверка регенерации", building = "Тестовое здание",
            drawing_title = "Лист А1", organization = "CAD MCP", sheet = "1", sheets = "1" } };
        var cells = new List<object> { new { cell = "A1", value = "Поз." }, new { cell = "B1", value = "Кол." }, new { cell = "C1", value = "Масса, кг" } };
        for (int row = 2; row <= 10; row++)
        {
            cells.Add(new { cell = "A" + row, value = (row - 1).ToString() });
            cells.Add(new { cell = "B" + row, value = (object)row });
            cells.Add(new { cell = "C" + row, value = (object)(row * 1.5) });
        }
        cells.Add(new { cell = "A11", value = "Итого" });
        cells.Add(new { cell = "B11", formula = "=Sum(B2:B10)" });
        cells.Add(new { cell = "C11", formula = "=Sum(C2:C10)" });
        var table = new object[] { new { op = "table_create", id = "spec", layout = Layout, position = new[] { 420, 400, 0 }, rows = 11, columns = 3, column_widths = new[] { 20, 30, 40 }, cells } };
        var crossTable = new object[]
        {
            table[0],
            new { op = "table_create", layout = Layout, position = new[] { 420, 250, 0 }, rows = 1, columns = 1,
                cells = new object[] { new { cell = "A1", formula = "={{total}}*2", references = new[] { new { name = "total", table_target = "spec", cell = "C11" } } } } }
        };
        var notes = new object[] { new { op = "mtext", layout = Layout, position = new[] { 420, 120, 0 }, text = "Примечания:\\P1. Размеры в миллиметрах.\\P2. Отметки в метрах.", height = 3.5, width = 180 } };
        var all = frame.Concat(table).Concat(notes).ToArray();
        string J(object[] plan) => JsonSerializer.Serialize(plan);
        var variants = new List<(string, string, bool, string?)>
        {
            ("frame", J(frame), false, null), ("table", J(table), false, null), ("cross_table", J(crossTable), false, null), ("notes", J(notes), false, null),
            ("all", J(all), false, null), ("all_after_preview", J(all), true, null), ("cross_table_after_preview", J(crossTable), true, null),
            ("render_then_edit", J(all), false, "render"), ("frame_explode", J(frame), false, "explode")
        };
        if (Environment.GetEnvironmentVariable("CADMCP_SHEET_PLAN") is { Length: > 0 } path)
        {
            using var parsed = JsonDocument.Parse(File.ReadAllText(path));
            var root = parsed.RootElement;
            // An operation receipt keeps the request: Request.Data.operations_json.
            string plan = root.ValueKind == JsonValueKind.Array ? root.GetRawText()
                : root.GetProperty("Request").GetProperty("Data").Text("operations_json") ?? throw new InvalidDataException("No operations_json in " + path);
            variants.Add(("user_plan", plan, false, null));
            variants.Add(("user_plan_after_preview", plan, true, null));
        }
        return variants;
    }
}
