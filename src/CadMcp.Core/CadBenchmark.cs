using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CadMcp.Core;

public sealed record CadBenchmarkProgress(string Message, string? DocumentId = null);
public sealed record CadBenchmarkCall(Request Request, Response? Response, double ElapsedMs, string? TransportError = null);
public sealed record CadBenchmarkCheck(string Name, bool Passed, object? Expected, object? Actual);
public sealed record CadBenchmarkCase(string Id, string Name, string Language, string Status, double ElapsedMs,
    double ApprovalWaitMs, string? ErrorCode, string? Message, IReadOnlyList<CadBenchmarkCheck> Checks, IReadOnlyList<CadBenchmarkCall> Calls);
public sealed record CadBenchmarkResult(string RunId, string SessionId, string? DocumentId, string? FixturePath,
    DateTimeOffset StartedAt, double ElapsedMs, IReadOnlyList<CadBenchmarkCase> Cases, string? SetupError = null,
    IReadOnlyList<CadBenchmarkCall>? SetupCalls = null, IReadOnlyList<string>? ToolInventory = null)
{
    public int Total => Cases.Count;
    public int Passed => Cases.Count(c => c.Status == "passed");
    public int Failed => Cases.Count(c => c.Status == "failed");
    public int Skipped => Cases.Count(c => c.Status == "skipped");
    public int Unknown => Cases.Count(c => c.Status == "unknown");
    public bool Complete => Total > 0 && Passed == Total && SetupError is null;
    public int ToolsPassed => ToolInventory?.Count(t => Cases.Any(c => c.Id == "tool_" + t && c.Status == "passed")) ?? 0;
    public int ToolsSkipped => ToolInventory?.Count(t => Cases.Any(c => c.Id == "tool_" + t && c.Status == "skipped")) ?? 0;
    public int CadCalls => (SetupCalls?.Count ?? 0) + Cases.Sum(c => c.Calls.Count);
    public string Markdown()
    {
        var text = new StringBuilder($"**Бенчмарк CAD MCP: {Passed}/{Total} тестов пройдено**\n\n");
        text.AppendLine($"Общее время: {ElapsedMs / 1000:0.000} с. Запросов runner: {CadCalls}. Ошибок: {Failed}; пропущено: {Skipped}; неизвестный исход: {Unknown}.");
        if (ToolInventory is not null) text.AppendLine($"\nMCP-инструменты с пройденными проверками: {ToolsPassed}/{ToolInventory.Count}; пропущено по условиям: {ToolsSkipped}. Inventory получен через tools/list настоящего MCP host. Пропуски не являются подтверждённым покрытием.");
        if (SetupError is not null) text.AppendLine("\nПодготовка не выполнена: " + SetupError);
        text.AppendLine("\n| Тест | Результат | Время, с | Ожидание подтверждения, с |\n|---|---|---:|---:|");
        foreach (var item in Cases)
        {
            text.AppendLine($"| {item.Name} | {item.Status.ToUpperInvariant()} | {item.ElapsedMs / 1000:0.000} | {item.ApprovalWaitMs / 1000:0.000} |");
            if (item.ErrorCode is not null) text.AppendLine($"\n`{item.Id}`: `{item.ErrorCode}` — {item.Message?.Replace('\n', ' ')}\n");
        }
        text.AppendLine("\nВремя включает обращения к AutoCAD и ожидание AutoLISP; это фиксированная функциональная проверка, без вызовов моделей. Пропуски и неизвестные исходы не считаются успехом. Тестовый DWG оставлен открытым; рабочие чертежи не используются. Остановка ожидания не отменяет уже принятые CAD-операции.");
        return text.ToString();
    }
}

/// <summary>Fixed native C# and AutoLISP plans; all verdicts use separate actual geometry reads.</summary>
public sealed partial class CadBenchmark(Func<Request, CancellationToken, Task<Response>> send,
    TimeSpan? pollInterval = null, TimeSpan? operationTimeout = null, IReadOnlyList<string>? toolInventory = null, string? runId = null)
{
    private sealed class Fault(string code, string message, bool unknown = false, bool skipped = false) : Exception(message)
    { public string Code = code; public bool Unknown = unknown; public bool Skipped = skipped; }
    private static readonly (string Id, string Name, string Language)[] Definitions =
    [ ("polyline", "C#: контур и ведомость", "C#"), ("text", "C#: Unicode-тексты", "C#"),
      ("blocks", "C#: блоки и преобразования WCS", "C#"), ("transform", "C#: копирование и изменение", "C#"),
      ("tables", "C#: связанные таблицы", "C#"), ("paging", "C#: пагинация поиска", "C#"),
      ("csharp", "C#: генерация отрезка и окружности", "C#"), ("lisp", "AutoLISP: генерация отрезка и окружности", "AutoLISP") ];
    private int running;

    public async Task<CadBenchmarkResult> RunAsync(string sessionId, Action<CadBenchmarkProgress>? progress = null, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) throw new InvalidOperationException("Benchmark is already running");
        try { return await new Run(send, pollInterval ?? TimeSpan.FromSeconds(1), operationTimeout ?? TimeSpan.FromMinutes(12), sessionId, progress, toolInventory, runId).Execute(ct); }
        finally { Volatile.Write(ref running, 0); }
    }

    private sealed partial class Run(Func<Request, CancellationToken, Task<Response>> transport, TimeSpan poll, TimeSpan timeout,
        string session, Action<CadBenchmarkProgress>? progress, IReadOnlyList<string>? toolInventory, string? fixedRunId)
    {
        private readonly string id = fixedRunId ?? Guid.NewGuid().ToString("N");
        private string? document, fixturePath;
        private readonly List<CadBenchmarkCall> calls = [];
        private readonly List<CadBenchmarkCheck> checks = [];
        private double approvalMs;
        private int nextOperation;
        private string Owner => "benchmark-" + id;

        private async Task<Response> Call(string operation, object data, CancellationToken ct, long? revision = null, string? target = null)
        {
            var request = new Request(Guid.NewGuid().ToString("N"), operation, session, target ?? document, revision,
                Wire.Element(data), DateTimeOffset.UtcNow.AddSeconds(25), Owner);
            var watch = Stopwatch.StartNew();
            using var callTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct); callTimeout.CancelAfter(timeout);
            try
            {
                var response = await transport(request, callTimeout.Token).WaitAsync(callTimeout.Token);
                calls.Add(new(request, response, watch.Elapsed.TotalMilliseconds));
                if (response.RequestId != request.RequestId) throw new Fault("RESPONSE_MISMATCH", "Response request id changed", true);
                if (response.SessionId is { } s && s != session || document is not null && response.DocumentId is { } d && d != document ||
                    document is not null && response.Error is null && (response.SessionId != session || response.DocumentId != document))
                    throw new Fault("SCOPE_MISMATCH", "Response belongs to another session/drawing", true);
                return response;
            }
            catch (Exception e) when (e is not Fault)
            { calls.Add(new(request, null, watch.Elapsed.TotalMilliseconds, e.Message)); throw; }
        }
        private static JsonElement Data(Response response) => Wire.Element(response.Data ?? new { });
        private static void Success(Response response)
        {
            if (response.Error is { } error)
                throw new Fault(error.Code, error.Message, error.Code is "SESSION_MISMATCH" or "SESSION_NOT_FOUND" or "DOCUMENT_CLOSED" or "DOCUMENT_MISMATCH" or "BENCHMARK_SCOPE_MISMATCH" or "OPERATION_UNCERTAIN",
                    error.Code is "LISP_DISABLED" or "LISP_DENIED" or "LISP_APPROVAL_UNAVAILABLE");
            if (response.Status != "completed") throw new Fault("INCOMPLETE_RESPONSE", "Expected completed response, got " + response.Status);
        }
        private async Task<JsonElement> Read(string operation, object data, CancellationToken ct)
        {
            var response = await Call(operation, data, ct); Success(response);
            var value = Data(response);
            foreach (var node in new[] { value, value.TryGetProperty("result", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : value })
                if (node.TryGetProperty("partial", out var partial) && partial.ValueKind == JsonValueKind.True ||
                    node.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True || node.TryGetProperty("archive_id", out _))
                    throw new Fault("INCOMPLETE_EVIDENCE", "The actual read is partial, truncated or archived");
            return value;
        }
        private void Check(string name, bool passed, object? expected, object? actual)
        {
            checks.Add(new(name, passed, expected, actual));
            if (!passed) throw new Fault("CHECK_FAILED", name + ": " + JsonSerializer.Serialize(new { expected, actual }, Wire.Json));
        }
        private void Number(string name, double expected, double actual) => Check(name, double.IsFinite(actual) && Math.Abs(expected - actual) <= 1e-6, expected, actual);
        private static JsonElement Result(JsonElement value) => value.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object ? result : value;
        private static string Handle(JsonElement value, string alias) => Result(value).GetProperty("results").EnumerateArray().Single(r => r.Text("id") == alias).Text("handle") ?? throw new Fault("MISSING_HANDLE", alias);
        private void Point(string name, JsonElement actual, double x, double y, double z = 0)
        { Number(name + ".x", x, actual[0].GetDouble()); Number(name + ".y", y, actual[1].GetDouble()); Number(name + ".z", z, actual[2].GetDouble()); }
        private async Task<JsonElement> Search(object options, CancellationToken ct, bool paged = false)
        {
            var data = Result(await Read("cad_search", new { options_json = JsonSerializer.Serialize(options, Wire.Json) }, ct));
            if (!paged && data.GetProperty("pagination").TryGetProperty("next_offset", out var next) && next.ValueKind == JsonValueKind.Number)
                throw new Fault("INCOMPLETE_EVIDENCE", "Search has additional pages");
            return data;
        }
        private async Task<Response> Receipt(string operationId, CancellationToken ct)
        {
            var watch = Stopwatch.StartNew(); bool awaitingApproval = false; double last = 0;
            try
            {
                while (watch.Elapsed < timeout)
                {
                    ct.ThrowIfCancellationRequested();
                    var response = await Call("cad_operation_status", new { operation_id = operationId }, ct); Success(response);
                    var data = Data(response); double now = watch.Elapsed.TotalMilliseconds;
                    if (data.Text("operation_id") != operationId) throw new Fault("OPERATION_UNCERTAIN", "Receipt operation id changed", true);
                    if (awaitingApproval) approvalMs += now - last;
                    last = now; awaitingApproval = data.Text("phase") == "awaiting_user_approval";
                    if (awaitingApproval) progress?.Invoke(new("AutoLISP ожидает подтверждения в AutoCAD", document));
                    string state = data.Text("state") ?? "unknown";
                    if (state is "completed" or "failed" or "cancelled")
                    {
                        if (!data.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                            throw new Fault("OPERATION_UNCERTAIN", "Terminal receipt has no result", true);
                        var terminal = JsonSerializer.Deserialize<Response>(result.GetRawText(), Wire.Json) ?? throw new Fault("OPERATION_UNCERTAIN", "Unreadable receipt", true);
                        if (terminal.SessionId is { } s && s != session || terminal.DocumentId is { } d && d != document ||
                            terminal.Error is null && (terminal.SessionId != session || terminal.DocumentId != document))
                            throw new Fault("SCOPE_MISMATCH", "Receipt belongs to another drawing", true);
                        bool consistent = state switch
                        {
                            "completed" => terminal.Status == "completed" && terminal.Error is null,
                            "failed" => terminal.Status == "failed" && terminal.Error is not null,
                            "cancelled" => terminal.Error?.Code == "CANCELLED",
                            _ => false
                        };
                        if (!consistent || Data(terminal).Text("operation_id") is { } terminalId && terminalId != operationId)
                            throw new Fault("OPERATION_UNCERTAIN", "Receipt state and result disagree", true);
                        return terminal;
                    }
                    if (state is not ("queued" or "running")) throw new Fault("OPERATION_UNCERTAIN", "Operation state: " + state, true);
                    await Task.Delay(poll, ct);
                }
                throw new Fault("OPERATION_UNCERTAIN", "Operation did not finish before the benchmark deadline", true);
            }
            finally { if (awaitingApproval) approvalMs += Math.Max(0, watch.Elapsed.TotalMilliseconds - last); }
        }
        private async Task<JsonElement> Mutate(string operation, object payload, CancellationToken ct)
        {
            var context = await Call("cad_context", new { }, ct); Success(context);
            if (context.SessionId != session || context.DocumentId != document || context.Revision is null)
                throw new Fault("SCOPE_MISMATCH", "Benchmark document context changed", true);
            var contextData = Data(context);
            if (contextData.Text("units") != "Millimeters" || contextData.Text("space") != "model")
                throw new Fault("BENCHMARK_CONTEXT_CHANGED", "Benchmark requires Model space and millimeters", true);
            var values = Wire.Element(payload).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
            string operationId = "bench_" + id + "_" + nextOperation++;
            values["operation_id"] = operationId;
            Response response;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeout);
            try
            {
                try { response = await Call(operation, values, deadline.Token, context.Revision); }
                catch (Exception e) when (Wire.IsTransportFailure(e) && !ct.IsCancellationRequested)
                { response = await Receipt(operationId, deadline.Token); }
                if (response.Status is "queued" or "pending" || response.Error?.Code == "TIMEOUT") response = await Receipt(operationId, deadline.Token);
                Success(response); return Data(response);
            }
            catch (OperationCanceledException) { throw new Fault("OPERATION_UNCERTAIN", "Stopped waiting; an accepted operation may still execute", true); }
            catch (Exception e) when (Wire.IsTransportFailure(e)) { throw new Fault("OPERATION_UNCERTAIN", e.Message, true); }
        }
        private Task<JsonElement> Edit(object[] operations, CancellationToken ct, int? count = null) => Mutate("cad_edit", new
        { operations_json = JsonSerializer.Serialize(operations, Wire.Json), expectations_json = count is null ? null : JsonSerializer.Serialize(new { entity_count = count.Value, enforce = true }) }, ct);

        public async Task<CadBenchmarkResult> Execute(CancellationToken ct)
        {
            var start = DateTimeOffset.UtcNow; var watch = Stopwatch.StartNew(); var results = new List<CadBenchmarkCase>(); string? setupError = null;
            bool stopped = false;
            try
            {
                if (toolInventory is not null && (toolInventory.Count == 0 || toolInventory.Distinct(StringComparer.Ordinal).Count() != toolInventory.Count))
                    throw new Fault("INVALID_TOOL_INVENTORY", "tools/list must contain nonempty unique tool names");
                progress?.Invoke(new("Создаю отдельный тестовый DWG"));
                var before = await Call("cad_documents", new { }, ct); Success(before);
                var oldIds = Data(before).EnumerateArray().Select(d => d.Text("document_id")).ToHashSet();
                var fixture = await Call("benchmark_create", new { run_id = id }, ct); Success(fixture);
                var data = Data(fixture);
                if (fixture.SessionId != session || string.IsNullOrWhiteSpace(fixture.DocumentId) || oldIds.Contains(fixture.DocumentId) ||
                    data.Text("run_id") != id || !data.TryGetProperty("benchmark_fixture", out var flag) || flag.ValueKind != JsonValueKind.True)
                    throw new Fault("INVALID_FIXTURE", "The worker did not create a new isolated benchmark drawing", true);
                document = fixture.DocumentId; fixturePath = data.Text("path");
                progress?.Invoke(new("Тестовый DWG создан", document));
            }
            catch (Exception e) { setupError = e.Message; stopped = true; }
            var setupCalls = calls.ToArray(); calls.Clear();
            foreach (var definition in Definitions)
            {
                if (stopped || ct.IsCancellationRequested)
                { results.Add(new(definition.Id, definition.Name, definition.Language, "skipped", 0, 0, "NOT_RUN", setupError ?? "Stopped after cancellation or unknown operation outcome", [], [])); continue; }
                progress?.Invoke(new("Бенчмарк: " + definition.Name, document));
                var caseWatch = Stopwatch.StartNew(); checks.Clear(); calls.Clear(); approvalMs = 0;
                string status = "passed"; string? errorCode = null, message = null;
                try
                {
                    switch (definition.Id)
                    {
                        case "polyline": await Polyline(ct); break;
                        case "text": await Text(ct); break;
                        case "blocks": await Blocks(ct); break;
                        case "transform": await Transform(ct); break;
                        case "tables": await Tables(ct); break;
                        case "paging": await Paging(ct); break;
                        case "csharp": await Generate(false, ct); break;
                        case "lisp": await Generate(true, ct); break;
                    }
                }
                catch (Fault error) { status = error.Unknown ? "unknown" : error.Skipped ? "skipped" : "failed"; errorCode = error.Code; message = error.Message; stopped = error.Unknown; }
                catch (OperationCanceledException error) { status = "unknown"; errorCode = "CANCELLED_WAIT"; message = error.Message; stopped = true; }
                catch (Exception error) { status = "failed"; errorCode = "BENCHMARK_ERROR"; message = error.Message; }
                results.Add(new(definition.Id, definition.Name, definition.Language, status, caseWatch.Elapsed.TotalMilliseconds, approvalMs, errorCode, message, checks.ToArray(), calls.ToArray()));
            }
            if (toolInventory is not null)
            {
                try { await Extended(results, stopped, ct); }
                catch (Exception error)
                {
                    results.Add(new("full_setup", "Подготовка полного набора", "MCP", "failed", 0, 0, "FULL_SETUP_FAILED", error.Message, [], []));
                    foreach (string tool in toolInventory.Where(t => results.All(c => c.Id != "tool_" + t)))
                        results.Add(new("tool_" + tool, tool, "MCP", "skipped", 0, 0, "NOT_RUN", "Full suite setup did not complete", [], []));
                }
            }
            return new(id, session, document, fixturePath, start, watch.Elapsed.TotalMilliseconds, results, setupError, setupCalls, toolInventory);
        }

        private async Task Polyline(CancellationToken ct)
        {
            var made = await Edit([new { op = "layer", name = "B_POLY" }, new { op = "polyline", id = "poly", layer = "B_POLY", points = new[] { new[] { 1000, 1000 }, new[] { 1200, 1000 }, new[] { 1200, 1100 }, new[] { 1000, 1100 } }, closed = true }], ct, 1);
            var entity = await Read("cad_entity_get", new { handle = Handle(made, "poly") }, ct);
            Check("closed", entity.GetProperty("closed").GetBoolean(), true, entity.GetProperty("closed"));
            Number("vertices", 4, entity.GetProperty("vertex_count").GetDouble()); Number("length", 600, entity.GetProperty("length").GetDouble()); Number("area", 20000, entity.GetProperty("area").GetDouble());
            var takeoff = await Read("cad_takeoff", new { scope = "model", layers_json = "[\"B_POLY\"]", include = "lengths,areas" }, ct);
            Number("takeoff.length", 600, takeoff.GetProperty("total_length").GetDouble()); Number("takeoff.area", 20000, takeoff.GetProperty("total_area").GetDouble());
        }
        private async Task Text(CancellationToken ct)
        {
            await Edit([new { op = "layer", name = "B_TEXT" }, new { op = "text", position = new[] { 2000, 0 }, height = 10, text = "Труба Ø108", layer = "B_TEXT" }, new { op = "text", position = new[] { 2000, 100 }, height = 10, text = "Труба Ø108", layer = "B_TEXT" }, new { op = "mtext", position = new[] { 2000, 200 }, height = 10, text = "Труба Ø108", layer = "B_TEXT" }], ct, 3);
            await Edit([new { op = "text_replace", find = "Ø108", replace = "Ø114", layers = new[] { "B_TEXT" }, scope = "model", match_case = true }], ct, 3);
            var old = await Search(new { scope = "model", layer = "B_TEXT", text = "Ø108", limit = 500 }, ct);
            Number("old texts", 0, old.GetProperty("entities").GetArrayLength());
            var current = (await Search(new { scope = "model", layer = "B_TEXT", text = "Ø114", details = true, limit = 500 }, ct)).GetProperty("entities").EnumerateArray().ToArray();
            Number("new texts", 3, current.Length);
            Check("full Unicode text", current.All(e => e.Text("text") == "Труба Ø114"), "Труба Ø114", current.Select(e => e.Text("text")).ToArray());
            Number("DBText count", 2, current.Count(e => e.Text("type") == "DBText")); Number("MText count", 1, current.Count(e => e.Text("type") == "MText"));
        }
        private async Task Blocks(CancellationToken ct)
        {
            var seed = await Edit([new { op = "layer", name = "B_BLOCKS" }, new { op = "line", id = "a", start = new[] { 3000, 0 }, end = new[] { 3100, 0 }, layer = "B_BLOCKS" }, new { op = "line", id = "b", start = new[] { 3000, 0 }, end = new[] { 3000, 50 }, layer = "B_BLOCKS" }], ct, 2);
            var made = await Edit([new { op = "block_define", name = "B_DEF", base_point = new[] { 3000, 0 }, handles = new[] { Handle(seed, "a"), Handle(seed, "b") } }, new { op = "block", id = "one", name = "B_DEF", position = new[] { 3500, 0 }, rotation_deg = 0, layer = "B_BLOCKS" }, new { op = "block", id = "two", name = "B_DEF", position = new[] { 3700, 0 }, rotation_deg = 90, layer = "B_BLOCKS" }], ct);
            var entities = (await Search(new { scope = "model", layer = "B_BLOCKS", expand_blocks = true, details = true, limit = 500 }, ct)).GetProperty("entities").EnumerateArray().ToArray();
            foreach (var (alias, x, rotate) in new[] { ("one", 3500, false), ("two", 3700, true) })
            {
                var nested = entities.Where(e => e.Text("root_handle") == Handle(made, alias) && e.Number("depth", 0) == 1).ToArray();
                Number(alias + " nested lines", 2, nested.Length);
                Number(alias + " long line count", 1, nested.Count(e => Math.Abs(e.GetProperty("length").GetDouble() - 100) < 1e-6));
                Number(alias + " short line count", 1, nested.Count(e => Math.Abs(e.GetProperty("length").GetDouble() - 50) < 1e-6));
                foreach (var e in nested)
                {
                    Point(alias + " start", e.GetProperty("start"), x, 0);
                    if (Math.Abs(e.GetProperty("length").GetDouble() - 100) < 1e-6) Point(alias + " long end", e.GetProperty("end"), rotate ? x : x + 100, rotate ? 100 : 0);
                    else { Number(alias + " short length", 50, e.GetProperty("length").GetDouble()); Point(alias + " short end", e.GetProperty("end"), rotate ? x - 50 : x, rotate ? 0 : 50); }
                }
            }
        }
        private async Task Transform(CancellationToken ct)
        {
            var seed = await Edit([new { op = "layer", name = "B_TRANSFORM" }, new { op = "line", id = "source", start = new[] { 4000, 0 }, end = new[] { 4100, 0 }, layer = "B_TRANSFORM" }], ct, 1);
            string original = Handle(seed, "source");
            var made = await Edit([new { op = "copy", id = "copy", handle = original, displacement = new[] { 0, 200 } }, new { op = "rotate", target = "copy", center = new[] { 4000, 200 }, angle_deg = 90 }, new { op = "scale", target = "copy", center = new[] { 4000, 200 }, factor = 2 }, new { op = "move", handle = original, displacement = new[] { 50, 0 } }], ct);
            var moved = await Read("cad_entity_get", new { handle = original }, ct);
            Point("moved.start", moved.GetProperty("start"), 4050, 0); Point("moved.end", moved.GetProperty("end"), 4150, 0);
            await Edit([new { op = "erase", handle = original }], ct);
            var e = await Read("cad_entity_get", new { handle = Handle(made, "copy") }, ct);
            Number("copy.length", 200, e.GetProperty("length").GetDouble()); Point("copy.start", e.GetProperty("start"), 4000, 200); Point("copy.end", e.GetProperty("end"), 4000, 400);
            var removed = await Call("cad_entity_get", new { handle = original }, ct);
            Check("source erased", removed.Error?.Code == "ENTITY_NOT_FOUND", "ENTITY_NOT_FOUND", removed.Error?.Code);
        }
        private async Task Tables(CancellationToken ct)
        {
            var made = await Edit([new { op = "layer", name = "B_TABLES" }, new { op = "table_create", id = "source", position = new[] { 5000, 0 }, rows = 3, columns = 3, layer = "B_TABLES", cells = new object[] { new { cell = "A1", value = 4 }, new { cell = "B1", value = 2.5 }, new { cell = "C1", formula = "=A1*B1" } } }, new { op = "table_create", id = "dependent", position = new[] { 5200, 0 }, rows = 2, columns = 2, layer = "B_TABLES", cells = new[] { new { cell = "A1", formula = "={{mass}}*2", references = new[] { new { name = "mass", table_target = "source", cell = "C1" } } } } }], ct, 2);
            string source = Handle(made, "source"), dependent = Handle(made, "dependent");
            async Task Value(string handle, string cell, double expected)
            { var table = await Read("cad_table_get", new { handle }, ct); Number(handle + "." + cell, expected, table.GetProperty("cells").EnumerateArray().Single(c => c.Text("cell") == cell).GetProperty("value").GetDouble()); }
            await Value(source, "C1", 10); await Value(dependent, "A1", 20);
            await Edit([new { op = "table_cells", handle = source, cells = new[] { new { cell = "A1", value = 6 } } }], ct, 1);
            await Value(source, "C1", 15); await Value(dependent, "A1", 30);
        }
        private async Task Paging(CancellationToken ct)
        {
            var operations = new List<object> { new { op = "layer", name = "B_PAGES" } };
            operations.AddRange(Enumerable.Range(0, 12).Select(i => (object)new { op = "point", id = "p" + i, position = new[] { 6000 + 10 * i, 0 }, layer = "B_PAGES" }));
            var made = await Edit(operations.ToArray(), ct, 12);
            var expected = Enumerable.Range(0, 12).Select(i => Handle(made, "p" + i)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var found = new List<string>(); int offset = 0;
            foreach (int count in new[] { 5, 5, 2 })
            {
                var page = await Search(new { scope = "model", layer = "B_PAGES", type = "POINT", limit = 5, offset, details = true }, ct, true);
                Number("page size", count, page.GetProperty("entities").GetArrayLength());
                foreach (var point in page.GetProperty("entities").EnumerateArray())
                {
                    int index = Enumerable.Range(0, 12).SingleOrDefault(i => Handle(made, "p" + i) == point.Text("handle"), -1);
                    Check("point type and identity", index >= 0 && point.Text("type") == "DBPoint" && point.Text("layer") == "B_PAGES", "B_PAGES DBPoint", point);
                    Point("point position", point.GetProperty("position"), 6000 + 10 * index, 0);
                }
                found.AddRange(page.GetProperty("entities").EnumerateArray().Select(e => e.Text("handle")!));
                bool more = page.GetProperty("pagination").TryGetProperty("next_offset", out var next) && next.ValueKind == JsonValueKind.Number;
                Check("pagination completion", more == (count == 5), count == 5, more);
                if (more) { Number("next offset", offset + count, next.GetDouble()); offset = next.GetInt32(); }
            }
            Check("unique handles", found.Count == 12 && found.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 12 && expected.SetEquals(found), expected.ToArray(), found.ToArray());
        }
        private async Task Generate(bool lisp, CancellationToken ct)
        {
            string layer = lisp ? "B_LISP" : "B_CSHARP"; int y = lisp ? 5000 : 4000;
            if (!lisp)
                await Edit([new { op = "layer", name = layer }, new { op = "line", start = new[] { 0, y }, end = new[] { 100, y }, layer }, new { op = "circle", center = new[] { 50, y + 50 }, radius = 10, layer }], ct, 2);
            else
            {
                const string code = "(if (not (tblsearch \"LAYER\" \"B_LISP\")) (entmake '((0 . \"LAYER\") (2 . \"B_LISP\") (70 . 0))))\n(entmake '((0 . \"LINE\") (8 . \"B_LISP\") (10 0.0 5000.0 0.0) (11 100.0 5000.0 0.0)))\n(entmake '((0 . \"CIRCLE\") (8 . \"B_LISP\") (10 50.0 5050.0 0.0) (40 . 10.0)))\n(princ \"CAD MCP benchmark\")";
                await Mutate("cad_lisp", new { code }, ct);
            }
            var entities = (await Search(new { scope = "model", layer, details = true, limit = 500 }, ct)).GetProperty("entities").EnumerateArray().ToArray();
            Number("generated entities", 2, entities.Length);
            var line = entities.Single(e => e.Text("type") == "Line"); var circle = entities.Single(e => e.Text("type") == "Circle");
            Number("generated line length", 100, line.GetProperty("length").GetDouble()); Point("generated line start", line.GetProperty("start"), 0, y); Point("generated line end", line.GetProperty("end"), 100, y);
            Number("generated circle radius", 10, circle.GetProperty("radius").GetDouble()); Point("generated circle center", circle.GetProperty("center"), 50, y + 50);
        }
    }
}
