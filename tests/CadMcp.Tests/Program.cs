using System.Diagnostics;
using System.Text.Json;
using CadMcp.Core;
using CadMcp.Providers;

try
{
Console.InputEncoding = new System.Text.UTF8Encoding(false);
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
if(args.Contains("--live-subagent-test"))
{
    int index=Array.IndexOf(args,"--live-subagent-test");await CadMcp.Tests.SubagentProbe.Run(args[index+1],args[index+2]);return;
}
if (args.Contains("--live-codex-model-test"))
{
    var resumeIndex = Array.IndexOf(args, "--resume-conversation");
    await CadMcp.Tests.CodexModelProbe.RunAsync(args[Array.IndexOf(args, "--live-codex-model-test") + 1], resumeIndex < 0 ? null : args[resumeIndex + 1]);
    return;
}

// Opt-in integration probe uses the official CLI with a recording CAD broker.
// It cannot connect to a real drawing and is excluded from the default test run.
if (args.Contains("--live-codex-approval-test"))
{
    await CadMcp.Tests.CodexApprovalProbe.RunAsync(args[Array.IndexOf(args, "--live-codex-approval-test") + 1]);
    return;
}

if(args.Contains("--cli-config-check"))
{
    string cli=args[Array.IndexOf(args,"--cli-config-check")+1];using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(35));
    var models=await CodexCatalog.ReadAsync(new(cli,Path.GetFullPath("src/CadMcp.Host/bin/Release/net8.0-windows/CadMcp.Host.exe"),Environment.CurrentDirectory,OwnerId:"config-probe",CadSessionId:"fixture-session",CadDocumentId:"fixture-document"),timeout.Token);
    Console.WriteLine("Official CLI parsed pinned MCP and custom read-only reviewer config; model count: "+models.Count);return;
}
// Child mode simulates only provider wire protocols; never invokes a real CLI/model.
if (args.Contains("app-server"))
{
    string? threadModel = null;
    int writerErrors = 0;
    foreach (var tool in new[] { "cad_edit", "cad_lisp", "cad_focus" })
        if (!args.Contains($"mcp_servers.cad.tools.{tool}.approval_mode=\"approve\""))
            throw new Exception("CAD editing permission was not configured: " + tool);
    while (Console.ReadLine() is { } line)
    {
        var e = JsonDocument.Parse(line).RootElement;
        var method = e.GetProperty("method").GetString();
        if (method == "initialize") Console.WriteLine("{\"id\":1,\"result\":{}}");
        if (method == "model/list")
        {
            bool secondPage = e.GetProperty("params").TryGetProperty("cursor", out _);
            Console.WriteLine(JsonSerializer.Serialize(new { id = 4, result = new {
                data = secondPage ? new object[] {
                    new { model = "available-model", isDefault = true, hidden = false, defaultReasoningEffort = "medium", supportedReasoningEfforts = new[] { new { reasoningEffort = "medium" } } },
                    new { model = "alternative-model", isDefault = false, hidden = false, defaultReasoningEffort = "low", supportedReasoningEfforts = new[] { new { reasoningEffort = "low" }, new { reasoningEffort = "high" } } } }
                    : new object[] { new { model = "hidden-model", isDefault = true, hidden = true, defaultReasoningEffort = "low" } },
                nextCursor = secondPage ? null : "page2" } }));
        }
        if (method is "thread/start" or "thread/resume" or "turn/start")
        {
            if (!e.GetProperty("params").TryGetProperty("model", out var model) || model.GetString() is not ("available-model" or "alternative-model"))
                throw new Exception("Provider inherited unavailable configured model");
        }
        if (method is "thread/start" or "thread/resume")
        {
            threadModel = e.GetProperty("params").GetProperty("model").GetString();
            if (!e.GetProperty("params").GetProperty("developerInstructions").GetString()!.Contains("cad_edit_help")) throw new Exception("Missing stable CAD instructions");
            if (e.GetProperty("params").GetProperty("approvalPolicy").GetString() != "never" ||
                e.GetProperty("params").GetProperty("sandbox").GetString() != "read-only")
                throw new Exception("CAD permission fix widened filesystem permissions");
        }
        if (method == "thread/resume" && e.GetProperty("params").GetProperty("threadId").GetString() == "retry-thread" && writerErrors++ == 0)
            Console.WriteLine("{\"id\":2,\"error\":{\"code\":-32600,\"message\":\"thread already has an active writer\"}}");
        else if (method is "thread/start" or "thread/resume") Console.WriteLine("{\"id\":2,\"result\":{\"thread\":{\"id\":\"test-thread\"}}}");
        if (method == "turn/start")
        {
            var inputs = e.GetProperty("params").GetProperty("input");
            var prompt = inputs[0].GetProperty("text").GetString();
            if (prompt!.StartsWith("ATTACHMENT"))
            {
                if (!prompt.Contains("Чертёж ✓") || !inputs.EnumerateArray().Any(x => x.GetProperty("type").GetString() == "localImage" && File.Exists(x.GetProperty("path").GetString())))
                    throw new Exception("Codex did not receive text and native image attachment");
            }
            bool explicitSelection = prompt == "SELECT";
            if (e.GetProperty("params").GetProperty("model").GetString() != (explicitSelection ? "alternative-model" : "available-model") ||
                threadModel != (explicitSelection ? "alternative-model" : "available-model") ||
                e.GetProperty("params").GetProperty("effort").GetString() != (explicitSelection ? "high" : "medium")) throw new Exception("Wrong requested model or effort on wire");
            if (prompt is "STEER" or "STEER_REJECT")
            {
                Console.WriteLine("{\"id\":3,\"result\":{\"turn\":{\"id\":\"live-turn\"}}}");
                var next=JsonDocument.Parse(Console.ReadLine()!).RootElement;
                if(next.GetProperty("method").GetString()!="turn/steer"||next.GetProperty("params").GetProperty("expectedTurnId").GetString()!="live-turn"||next.GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString()!="Уточнение ✓")throw new Exception("Invalid same-turn steering contract");
                int inputId=next.GetProperty("id").GetInt32();
                Console.WriteLine(prompt=="STEER"?JsonSerializer.Serialize(new{id=inputId,result=new{turnId="live-turn"}}):JsonSerializer.Serialize(new{id=inputId,error=new{code=-32600,message="turn already completed"}}));
                Console.WriteLine("{\"method\":\"item/agentMessage/delta\",\"params\":{\"delta\":\"Учёл уточнение\"}}");
                Console.WriteLine("{\"method\":\"turn/completed\",\"params\":{\"turn\":{\"status\":\"completed\"}}}");
                return;
            }
            if (prompt == "SILENT") { await Task.Delay(TimeSpan.FromMinutes(1)); return; }
            Console.WriteLine("{\"id\":3,\"result\":{}}");
            Console.WriteLine("{\"method\":\"item/reasoning/summaryTextDelta\",\"params\":{\"delta\":\"Проверяю размеры чертежа\",\"itemId\":\"r1\",\"summaryIndex\":0,\"threadId\":\"test-thread\",\"turnId\":\"t1\"}}");
            Console.WriteLine("{\"method\":\"item/started\",\"params\":{\"item\":{\"type\":\"mcpToolCall\",\"tool\":\"cad_search\"}}}");
            Console.WriteLine("{\"method\":\"item/completed\",\"params\":{\"item\":{\"type\":\"mcpToolCall\",\"tool\":\"cad_search\",\"status\":\"completed\"}}}");
            Console.WriteLine("{\"method\":\"item/started\",\"params\":{\"threadId\":\"test-thread\",\"item\":{\"type\":\"collabAgentToolCall\",\"tool\":\"spawnAgent\"}}}");
            Console.WriteLine("{\"method\":\"item/agentMessage/delta\",\"params\":{\"threadId\":\"child-thread\",\"delta\":\"CHILD-ONLY\"}}");
            Console.WriteLine("{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"child-thread\",\"turn\":{\"status\":\"completed\"}}}");
            Console.WriteLine("{\"method\":\"item/completed\",\"params\":{\"threadId\":\"test-thread\",\"item\":{\"type\":\"collabAgentToolCall\",\"tool\":\"wait\",\"status\":\"completed\"}}}");
            Console.WriteLine("{\"method\":\"item/agentMessage/delta\",\"params\":{\"delta\":\"Сеть ✓\"}}");
            Console.WriteLine("{\"method\":\"turn/completed\",\"params\":{\"turn\":{\"status\":\"completed\"}}}");
        }
    }
    return;
}
if (args.Contains("-p"))
{
    string inputLine=Console.ReadLine()!;
    var input=JsonDocument.Parse(inputLine).RootElement;
    var blocks=input.GetProperty("message").GetProperty("content");
    string prompt=blocks[0].GetProperty("text").GetString()!;
    if(prompt.StartsWith("ATTACHMENT")&&!blocks.EnumerateArray().Any(x=>x.GetProperty("type").GetString()=="image"&&Convert.FromBase64String(x.GetProperty("source").GetProperty("data").GetString()!).Length>8))throw new Exception("Claude image input missing");
    if(prompt=="STEER")
    {
        var next=JsonDocument.Parse(Console.ReadLine()!).RootElement;
        if(next.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString()!="Уточнение ✓")throw new Exception("Claude live input lost Unicode");
        Console.WriteLine(next.GetRawText());
        Console.WriteLine("{\"type\":\"result\",\"is_error\":false,\"result\":\"Первый результат\"}");
        Console.WriteLine("{\"type\":\"result\",\"is_error\":false,\"result\":\"Учёл уточнение\"}");
        return;
    }
    if (prompt == "SILENT") { await Task.Delay(TimeSpan.FromMinutes(1)); return; }
    Console.WriteLine("{\"type\":\"system\",\"session_id\":\"test-claude\"}");
    Console.WriteLine("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"tool-1\",\"name\":\"mcp__cad__cad_edit\"}]}}");
    Console.WriteLine("{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tool-1\",\"content\":\"ok\"}]}}");
    Console.WriteLine("{\"type\":\"stream_event\",\"event\":{\"delta\":{\"text\":\"Сеть ✓\"}}}");
    Console.WriteLine("{\"type\":\"result\",\"is_error\":false,\"result\":\"Сеть ✓\"}");
    return;
}

int passed = 0;
async Task Test(string name, Func<Task> test)
{
    await test(); Console.WriteLine("PASS " + name); passed++;
}
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
await Test("frame Unicode roundtrip", async () =>
{
    using var stream = new MemoryStream(); var bytes = System.Text.Encoding.UTF8.GetBytes("СПДС — сеть ✓");
    await Frames.WriteAsync(stream, bytes, default); stream.Position = 0;
    Assert((await Frames.ReadAsync(stream, default))!.SequenceEqual(bytes), "Frame corrupt");
});
await Test("reject oversized/truncated frame", async () =>
{
    await Throws<InvalidDataException>(() => Frames.ReadAsync(new MemoryStream(BitConverter.GetBytes(Frames.MaximumBytes + 1)), default));
    await Throws<EndOfStreamException>(() => Frames.ReadAsync(new MemoryStream([3, 0, 0, 0, 1]), default));
    await Throws<EndOfStreamException>(() => Frames.ReadAsync(new MemoryStream([3, 0]), default));
});
await Test("HTML chat escapes input and Word export embeds images", async () =>
{
    var folder = Path.Combine(Path.GetTempPath(), "cad-chat-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try
    {
        var png = Path.Combine(folder, "view.png");
        File.WriteAllBytes(png, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6wAAAABJRU5ErkJggg=="));
        var lines = new[] { new ChatLine("user", "<script>alert(1)</script>\n\nФото", Images: new[] { new ChatImage("view.png", png, 1, 1) }),
            new ChatLine("assistant", "Площадь $A=ab$ и **размер** 3,05 м", Steps: new[] { "Завершено: cad_search" }, ReasoningSummary: "Сверяю геометрию") };
        var html = ChatMarkup.ConversationHtml(lines, folder);
        Assert(!html.Contains("<script>") && html.Contains("&lt;script&gt;"), "HTML injection escaped incorrectly");
        Assert(html.Contains("https://cadmcp-assets.local/view.png"), "Image lost: " + html);
        Assert(html.Contains("class=\"math\"") && html.Contains("A=ab"), "Math lost: " + html);
        Assert(!html.Contains("cad_search"), "Raw CAD events should not appear in chat");
        Assert(!ChatMarkup.PlainTranscript(lines).Contains("cad_search"), "Raw CAD events should not appear in transcript");
        var path = Path.Combine(folder, "chat.docx");
        ChatWordExporter.Save(path, lines);
        using var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(path, false);
        Assert(document.MainDocumentPart!.ImageParts.Count() == 1, "Word image not embedded");
        Assert(document.MainDocumentPart.Document.Body!.InnerText.Contains("3,05 м"), "Word text missing");
        Assert(!document.MainDocumentPart.Document.Body.InnerText.Contains("cad_search"), "Raw CAD events should not appear in Word");
        var wordErrors=new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(document).ToArray();Assert(wordErrors.Length==0,"Invalid Word document: "+string.Join(";",wordErrors.Select(e=>e.Description+" @ "+e.Path?.XPath)));
    }
    finally { Directory.Delete(folder, true); }
    await Task.CompletedTask;
});
await Test("CAD activity is readable and visual work requests a preview", async () =>
{
    Assert(CadToolActivity.Describe("Выполняю mcp__cad__cad_search") == "Изучаю чертёж", "Search activity");
    Assert(CadToolActivity.Describe("Выполняю mcp__cad__cad_edit") == "Изменяю чертёж", "Edit activity");
    Assert(CadToolActivity.Describe("Выполняю mcp__cad__cad_render") == "Проверяю вид чертежа", "Render activity");
    Assert(CadToolActivity.ProducesVisibleResult("Завершено: mcp__cad__cad_edit"), "Edit should trigger preview");
    Assert(!CadToolActivity.ProducesVisibleResult("Ошибка: mcp__cad__cad_edit"), "Failed edit should not trigger preview");
    await Task.CompletedTask;
});
await Test("snapshot revision and document isolation", async () =>
{
    var store = new SnapshotStore(); var s = store.Add("d1", 4, [Wire.Element(new { handle = "A", layer = "Сеть", text = "Колодец" })], true);
    await Throws<CadFault>(() => Task.FromResult(store.Query(s.Id, "d2", 4, Wire.Element(new { }))));
    var historical = Wire.Element(store.Query(s.Id, "d1", 5, Wire.Element(new { })));
    Assert(historical.GetProperty("historical").GetBoolean() && historical.GetProperty("captured_revision").GetInt64() == 4,
        "Changed DWG did not mark the snapshot as historical");
    await Throws<CadFault>(() => Task.FromResult(store.Query(s.Id, "d1", 4, Wire.Element(new { limit = -1 }))));
    var result = Wire.Element(store.Query(s.Id, "d1", 4, Wire.Element(new { text = "колод", limit = 1 })));
    Assert(result.GetProperty("entities").GetArrayLength() == 1, "Unicode filter");
    Assert(!result.GetProperty("historical").GetBoolean(), "Current snapshot marked historical");
    Assert(result.GetProperty("pagination").GetProperty("snapshot_truncated").GetBoolean(), "Hidden truncation");
});
await Test("stable pagination and bounded snapshot retention", async () =>
{
    var store = new SnapshotStore(); var items = Enumerable.Range(0, 5).Select(i => Wire.Element(new { handle = i.ToString(), layer = "A" }));
    var s = store.Add("d", 1, items, false);
    var page = Wire.Element(store.Query(s.Id, "d", 1, Wire.Element(new { offset = 2, limit = 2 })));
    Assert(page.GetProperty("entities")[0].GetProperty("handle").GetString() == "2", "Wrong offset");
    Assert(page.GetProperty("pagination").GetProperty("next_offset").GetInt32() == 4, "Wrong next cursor");
    for (int i = 0; i < 4; i++) store.Add("d", 1, [], false);
    await Throws<CadFault>(() => Task.FromResult(store.Query(s.Id, "d", 1, Wire.Element(new { }))));
});
await Test("acceptance detects wrong actual geometry, units and missing measurements", async () =>
{
    var entity=Wire.Element(new{handle="A",type="Line",layer="Сеть",length=99d,start=new[]{0,0,0},end=new[]{99,0,0},bounds=new{min=new[]{0,0,0},max=new[]{99,0,0}}});
    var plan=DrawingVerification.Parse("""{"units":"Millimeters","entity_count":1,"checks":[{"target":"beam","property":"length","expected":100,"tolerance":0.1},{"handle":"A","property":"layer","expected":"Сеть"},{"handle":"A","property":"radius","expected":5}]}""");
    var result=DrawingVerification.Evaluate([entity],"Millimeters",plan,new Dictionary<string,string>{{"beam","A"}});
    Assert(result.Failed==1&&result.Passed==3&&result.Unverified==1,"Actual drift/missing measurement was accepted");
    var distance=DrawingVerification.Parse("""{"checks":[{"property":"distance","first":{"handle":"A","point":"start"},"second":{"handle":"A","point":"end"},"expected":99,"tolerance":0.0001}]}""");
    Assert(DrawingVerification.Evaluate([entity],"Millimeters",distance).State=="passed","Distance readback failed");
    Assert(DrawingVerification.Evaluate([entity],"Inches",plan,new Dictionary<string,string>{{"beam","A"}}).Failed==2,"Units mismatch ignored");
    await Throws<CadFault>(()=>Task.FromResult(DrawingVerification.Parse("""{"checks":[{"handle":"A","property":"length","expected":99,"tolerance":-1}]}""")));
});
await Test("whole task bounds, type counts, mass range and pending visual review use actual evidence",()=>
{
    var entities=new[]{Wire.Element(new{handle="A",type="BlockReference",assembly=new{solid_mass_kg=12d,profile_code="20Б1"},bounds=new{min=new[]{0,0,0},max=new[]{100,20,30}}}),Wire.Element(new{handle="B",type="Line",length=200d,bounds=new{min=new[]{100,0,0},max=new[]{300,0,0}}})};
    var plan=DrawingVerification.Parse("""{"task":"Frame","units":"Millimeters","bounds_size":[300,20,30],"type_counts":{"BlockReference":1,"Line":1},"checks":[{"handle":"A","property":"assembly.solid_mass_kg","minimum":11.9,"maximum":12.1},{"handle":"A","property":"assembly.profile_code","expected":"20Б1"}],"review_views":["front","isometric"],"visual_requirements":["silhouette"]}""");
    var report=DrawingVerification.Evaluate(entities,"Millimeters",plan);Assert(report.State=="passed"&&report.OverallState=="visual_review_required"&&report.Passed==6,"Measured contract lost a constraint or passed unreviewed visual requirements");
    var wrong=DrawingVerification.Evaluate(entities.Take(1),"Millimeters",plan);Assert(wrong.Failed>=2,"Subset verification accepted missing task geometry");return Task.CompletedTask;
});
await Test("invalid task contracts fail before native calls",async()=>
{
    foreach(string json in new[]{"""{"type_counts":{"Line":"1"}}""","""{"checks":[{"handle":"A","property":"length","minimum":2,"maximum":1}]}""","""{"checks":[{"handle":"A","property":"length","expected":1,"minimum":0}]}""","""{"review_views":["made-up"]}""","""{"bounds_size":[1,-2,3]}"""})await Throws<CadFault>(()=>Task.FromResult(DrawingVerification.Parse(json)));
});
await Test("native steel catalog converts millimetres independently of material grade",async()=>
{
    Assert(SteelSections.Get("20б1").RootRadiusMm==11&&Math.Abs(SteelSections.Get("PIPE-108x4").AreaMm2-Math.PI*(54*54-50*50))<1e-8,"Nominal section data is incorrect");
    Assert(SteelSections.MetersPerUnit("Meters")==1&&SteelSections.MetersPerUnit("Millimeters")==.001,"Catalog unit conversion is incorrect");
    await Throws<CadFault>(()=>Task.FromResult(SteelSections.Get("guessed-profile")));await Throws<CadFault>(()=>Task.FromResult(SteelSections.MetersPerUnit("Undefined")));
});
await Test("rounded rebar rejects overlapping bends and preserves spatial tangent length",async()=>
{
    var path=BendPath.Create([new[]{0d,0,0},new[]{100d,0,0},new[]{100d,0,100}],10);Assert(path.Bends.Count==1&&Math.Abs(path.Length-(180+5*Math.PI))<1e-8,"Spatial bend centerline length is incorrect");
    await Throws<CadFault>(()=>Task.FromResult(BendPath.Create([new[]{0d,0,0},new[]{10d,0,0},new[]{10d,10,0},new[]{20d,10,0}],8)));
});
await Test("custom Codex roles all receive pinned read-only MCP overrides",()=>
{
    string root=Path.Combine(Path.GetTempPath(),"cad-roles-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,".codex","agents"));
    try{
        File.WriteAllText(Path.Combine(root,".codex","config.toml"),"[agents.'planner']\n[agents.\"drafter\"]\n");File.WriteAllText(Path.Combine(root,".codex","agents","inspector.toml"),"name = 'inspector'\n");
        var option=new ProviderOptions("unused","unused",root,OwnerId:"owner",CadSessionId:"session",CadDocumentId:"drawing");
        var arguments=CadSubagentPolicy.Arguments(option);foreach(string role in new[]{"default","worker","explorer","cad_reviewer","planner","drafter","inspector"})
        {string assignment=arguments.Single(a=>a.StartsWith("agents."+role+".config_file="));string path=JsonSerializer.Deserialize<string>(assignment[(assignment.IndexOf('=')+1)..])!;string config=File.ReadAllText(path);Assert(config.Contains("sandbox_mode = \"read-only\"")&&config.Contains("CAD_MCP_READ_ONLY = \"1\"")&&config.Contains("CAD_MCP_DOCUMENT_ID = \"drawing\""),"Custom role acquired write access or lost its document scope");}
    }finally{Directory.Delete(root,true);}return Task.CompletedTask;
});
await Test("result handles include the whole response and exclude failed edits",()=>
{
    var operations=Wire.Element(new[]{new{state="completed",operation="cad_edit",handles=new[]{"A","B"}},new{state="completed",operation="cad_edit",handles=new[]{"a","C"}},new{state="failed",operation="cad_edit",handles=new[]{"D"}}});
    Assert(CadResultSummary.ChangedHandles(operations).SequenceEqual(new[]{"A","B","C"}),"Final geometry review considered only the last edit or duplicated a handle");return Task.CompletedTask;
});
await Test("photo calibration recovers perspective, scale, inverse and rejects degenerate anchors", async () =>
{
    double[] World(double u,double v) { double d=1+.0002*u+.0001*v; return [(100+2*u+.2*v)/d,(200-.1*u+1.5*v)/d,7]; }
    var pixels=new[]{new[]{0d,0d},new[]{1000d,0d},new[]{1000d,800d},new[]{0d,800d},new[]{200d,300d}};
    var fit=PhotoReference.Fit(Wire.Element(pixels.Select(p=>new{pixel=p,world=World(p[0],p[1])})),1000,800,"projective");
    var expected=World(350,275); var actual=fit.PixelToWorld(350,275);
    Assert(fit.RmsError<1e-7 && Math.Abs(expected[0]-actual[0])<1e-7 && Math.Abs(expected[1]-actual[1])<1e-7,"Perspective calibration lost precision");
    var inverse=fit.WorldToPixel(actual[0],actual[1]); Assert(Math.Abs(inverse[0]-350)<1e-7&&Math.Abs(inverse[1]-275)<1e-7,"Photo inverse failed");
    var similarity=PhotoReference.Fit(Wire.Element(new[]{new{pixel=new[]{0,0},world=new[]{100,200,0}},new{pixel=new[]{100,0},world=new[]{300,200,0}}}),1000,800,"similarity");
    var point=similarity.PixelToWorld(20,30); Assert(Math.Abs(point[0]-140)<1e-7&&Math.Abs(point[1]-140)<1e-7,"Top-left photo Y convention is wrong");
    await Throws<CadFault>(()=>Task.FromResult(PhotoReference.Fit(Wire.Element(Enumerable.Range(0,4).Select(i=>new{pixel=new[]{i*10,0},world=new[]{i*20,0,0}})),100,100,"projective")));
});
await Test("silhouette comparison rejects mismatching shapes and reports its evidence", () =>
{
    var a=Wire.Element(new[]{new[]{.1,.1},new[]{.8,.1},new[]{.8,.8},new[]{.1,.8}});
    var b=Wire.Element(new[]{new[]{.5,.5},new[]{.9,.5},new[]{.9,.9},new[]{.5,.9}});
    Assert(Wire.Element(ReferenceComparison.Compare(a,a,.95,.01)).Text("state")=="passed","Identical silhouettes failed");
    var result=Wire.Element(ReferenceComparison.Compare(a,b,.85,.05));
    Assert(result.Text("state")=="failed"&&result.Text("evidence")=="agent_supplied_contours_in_comparable_views","Different silhouette accepted"); return Task.CompletedTask;
});
await Test("lost mutation response retrieves receipt without resending drawing edits", async () =>
{
    var request=new Request("r","cad_edit","s","d",1,Wire.Element(new{operation_id="one",operations_json="[]"})); int edits=0,statuses=0;
    async Task<Response> Send(Request r,CancellationToken ct) { await Task.Yield(); if(r.Operation=="cad_edit"){edits++;throw new IOException("response lost after commit");}
        statuses++; return new(r.RequestId,"completed",new{state="completed",result=new Response("r","completed",new{transaction="committed"},"s","d",2)}); }
    var result=await MutationRecovery.CallAsync(request,Send,default);
    Assert(edits==1&&statuses==1&&result.Revision==2&&result.Error is null,"Recovery repeated mutation or lost confirmed receipt");
    var pending=await MutationRecovery.CallAsync(request,(r,ct)=>r.Operation=="cad_edit"?Task.FromException<Response>(new IOException("lost")):Task.FromResult(new Response(r.RequestId,"completed",new{state="running"})),default);
    Assert(pending.Status=="pending"&&Wire.Element(pending.Data!).Text("state")=="running","Running operation was treated as failed/retriable");
    var unknown=await MutationRecovery.CallAsync(request,(r,ct)=>Task.FromException<Response>(new IOException("unreachable")),default);
    Assert(unknown.Error?.Code=="OPERATION_UNCERTAIN","Unknown mutation not identified");
});
await Test("concurrent operation acceptance is unique and archived receipts survive missing worker", async () =>
{
    string root=Path.Combine(Path.GetTempPath(),"cadmcp-recovery-"+Guid.NewGuid().ToString("N")),session=Guid.NewGuid().ToString("N");
    var journal=new OperationJournal(Path.Combine(root,session)); var request=new Request("r","cad_edit",session,"d",1,Wire.Element(new{operation_id="shared"}));
    var accepted=await Task.WhenAll(Enumerable.Range(0,16).Select(i=>Task.Run(()=>journal.Begin("shared",request))));
    Assert(accepted.Count(e=>e is null)==1,"Concurrent retries created multiple accepted mutations");
    journal.Running("shared");
    var broker=new Broker(Path.Combine(root,"workers"),root);
    var status=await broker.DispatchAsync(new("status","cad_operation_status",session,"d",Data:Wire.Element(new{operation_id="shared"})),default);
    Assert(Wire.Element(status.Data!).Text("state")=="unknown"&&Wire.Element(status.Data!).GetProperty("historical").GetBoolean(),"Lost worker was reported as safe to retry");
    journal.Complete("shared",new("r","completed",new{transaction="committed"},session,"d",2));
    status=await broker.DispatchAsync(new("status","cad_operation_status",session,"d",Data:Wire.Element(new{operation_id="shared"})),default);
    Assert(Wire.Element(status.Data!).Text("state")=="completed","Archived confirmed result lost");
});
await Test("result summary distinguishes checked geometry and an unsaved DWG", () =>
{
    var list=Wire.Element(new{operations=new[]{new{state="completed",operation="cad_edit"}}});
    var report=Wire.Element(new{verification=new{state="passed",entity_count=4,erased_count=0,passed=2,failed=0,unverified=0},document_state=new{disk_save="unsaved"}});
    var text=CadResultSummary.Describe(list,report);
    Assert(text.Contains("объектов: 4")&&text.Contains("несохранённые изменения")&&!text.Contains("Выполняю cad_"),"User result is misleading or technical"); return Task.CompletedTask;
});
await Test("pipe stays responsive during a long CAD request and rejects expired requests", async () =>
{
    string pipe = "cadmcp-test-" + Guid.NewGuid().ToString("N");
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var server = new PipeServer(pipe, async (r, ct) =>
    {
        if (r.Operation == "slow") { started.SetResult(); await release.Task.WaitAsync(ct); }
        return new(r.RequestId, "completed", "ok");
    });
    server.Start();
    var slow = PipeClient.CallAsync(pipe, new("slow", "slow"), default);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    using var quickTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    var quick = await PipeClient.CallAsync(pipe, new("quick", "broker_ping"), quickTimeout.Token);
    Assert(quick.Status == "completed", "Broker ping was blocked by a long CAD call");
    release.SetResult();
    Assert((await slow).Status == "completed", "Long CAD call did not finish");
    var expired = await PipeClient.CallAsync(pipe, new("expired", "read", Deadline: DateTimeOffset.UtcNow.AddSeconds(-1)), default);
    Assert(expired.Error?.Code == "DEADLINE_EXPIRED", "Expired request ran");
});
await Test("broker rejects missing session", async () =>
{
    var broker = new Broker(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
    Assert(broker.Discover().Count == 0, "Unexpected workers");
    await Throws<CadFault>(() => broker.DispatchAsync(new("r", "cad_snapshot"), default));
});
await Test("broker probes independent CAD sessions in parallel", async () =>
{
    var root = Path.Combine(Path.GetTempPath(), "cad-broker-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var first = "cadmcp-worker-" + Guid.NewGuid().ToString("N");
    var second = "cadmcp-worker-" + Guid.NewGuid().ToString("N");
    int active = 0;
    var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    async Task<Response> Handler(Request r, CancellationToken ct)
    {
        if (Interlocked.Increment(ref active) == 2) bothStarted.TrySetResult();
        await bothStarted.Task.WaitAsync(ct);
        return new(r.RequestId, "completed", new { document = r.SessionId });
    }
    using var a = new PipeServer(first, Handler);
    using var b = new PipeServer(second, Handler);
    a.Start(); b.Start();
    try
    {
        File.WriteAllText(Path.Combine(root, "one.json"), JsonSerializer.Serialize(new WorkerDescriptor("s1", first, 1, "test"), Wire.Json));
        File.WriteAllText(Path.Combine(root, "two.json"), JsonSerializer.Serialize(new WorkerDescriptor("s2", second, 2, "test"), Wire.Json));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = await new Broker(root).DispatchAsync(new("sessions", "cad_sessions"), timeout.Token);
        var sessions = Wire.Element(result.Data!);
        Assert(sessions.GetArrayLength() == 2 && sessions.EnumerateArray().All(x => x.GetProperty("reachable").GetBoolean()), "CAD sessions were probed serially");
    }
    finally
    {
        File.Delete(Path.Combine(root, "one.json")); File.Delete(Path.Combine(root, "two.json")); Directory.Delete(root);
    }
});
await Test("edit plan supports aliases, Unicode and explicit units", () =>
{
    var plan = EditPlan.Parse("""
        [{"op":"layer","name":"Сеть","color_index":3},
         {"op":"polyline","id":"pipe","points":[[0,0,10],[100,0,10]],"bulges":[0,0],"layer":"Сеть"},
         {"op":"move","target":"pipe","displacement":[0,50]},
         {"op":"text","position":[0,50],"text":"Колодец ✓","height":2.5}]
        """);
    Assert(plan.Length == 4 && EditPlan.Point(plan[2].GetProperty("displacement"))[2] == 0, "Plan contract incorrect");
    return Task.CompletedTask;
});
await Test("native geometry, blocks and layouts validate before touching DWG", async () =>
{
    var plan = EditPlan.Parse("""
        [{"op":"point","position":[1,2]},
         {"op":"ellipse","center":[0,0],"major_axis":[10,0],"radius_ratio":0.5},
         {"op":"block_define","name":"STAIRS","base_point":[0,0],"handles":["A","B"]},
         {"op":"layout_create","name":"План А3"}]
        """);
    Assert(plan.Length == 4, "New C# operation contract is missing");
    foreach (var invalid in new[] { "[{\"op\":\"ellipse\",\"center\":[0,0],\"major_axis\":[10,0],\"radius_ratio\":2}]",
        "[{\"op\":\"block_define\",\"name\":\"X\",\"base_point\":[0,0],\"handles\":[\"NOT_HEX\"]}]" })
        await Throws<CadFault>(() => Task.FromResult(EditPlan.Parse(invalid)));
});
await Test("native spatial paths and meshes validate face topology", async () =>
{
    var plan = EditPlan.Parse("""
        [{"op":"polyline3d","points":[[0,0,0],[10,0,5],[10,10,10]]},
         {"op":"mesh","vertices":[[0,0,0],[10,0,0],[0,10,0],[0,0,10]],
          "faces":[[0,2,1],[0,1,3],[1,2,3],[2,0,3]]}]
        """);
    Assert(plan.Length == 2, "Native 3D edit operations missing");
    foreach (var invalid in new[] {
        "[{\"op\":\"polyline3d\",\"points\":[[0,0],[1,1,1]]}]",
        "[{\"op\":\"mesh\",\"vertices\":[[0,0,0],[1,0,0],[0,1,0]],\"faces\":[[0,1,3]]}]",
        "[{\"op\":\"mesh\",\"vertices\":[[0,0,0],[1,0,0],[0,1,0]],\"faces\":[[0,1,1]]}]",
        "[{\"op\":\"mesh\",\"vertices\":[[0,0,0],[1,0,0],[2,0,0]],\"faces\":[[0,1,2]]}]"
    }) await Throws<CadFault>(() => Task.FromResult(EditPlan.Parse(invalid)));
});
await Test("native solid modeling plans validate references and sizes", async () =>
{
    var plan = EditPlan.Parse("""
        [{"op":"circle","id":"profile","center":[0,0,0],"radius":5},
         {"op":"extrude","id":"solid","target":"profile","direction":[0,0,10]},
         {"op":"sphere","id":"tool","center":[0,0,5],"radius":2},
         {"op":"solid_boolean","target":"solid","tool_target":"tool","operation":"subtract"}]
        """);
    Assert(plan.Length == 4, "Native solid edit plan is missing");
    foreach (var invalid in new[] {
        "[{\"op\":\"extrude\",\"target\":\"future\",\"direction\":[0,0,10]}]",
        "[{\"op\":\"solid_boolean\",\"handle\":\"A\",\"operation\":\"union\"}]",
        "[{\"op\":\"torus\",\"center\":[0,0,0],\"major_radius\":-1,\"minor_radius\":1}]",
        "[{\"op\":\"solid_boolean\",\"handle\":\"A\",\"tool_handle\":\"B\",\"operation\":\"explode\"}]"
    }) await Throws<CadFault>(() => Task.FromResult(EditPlan.Parse(invalid)));
});
await Test("image control points fit pixel/WCS affine transform and inverse", () =>
{
    var points = Wire.Element(new[] {
        new { pixel = new[] { 0, 0 }, world = new[] { 1000, 2000, 0 } },
        new { pixel = new[] { 1000, 0 }, world = new[] { 1100, 2020, 0 } },
        new { pixel = new[] { 0, 500 }, world = new[] { 1025, 1900, 0 } },
        new { pixel = new[] { 500, 250 }, world = new[] { 1062, 1960, 0 } } });
    var fit = ImageRegistration.Fit(points, 1000, 500);
    var world = fit.PixelToWorld(500, 250);
    var pixel = fit.WorldToPixel(world[0], world[1]);
    Assert(Math.Abs(world[0] - 1062.5) < 1 && Math.Abs(pixel[0] - 500) < 1e-6 && Math.Abs(pixel[1] - 250) < 1e-6 && fit.RmsError > 0,
        "Image affine fit/inverse incorrect");
    try
    {
        ImageRegistration.Fit(Wire.Element(new[] {
            new { pixel = new[] { 0, 0 }, world = new[] { 0, 0, 0 } },
            new { pixel = new[] { 1, 1 }, world = new[] { 1, 1, 0 } },
            new { pixel = new[] { 2, 2 }, world = new[] { 2, 2, 0 } } }), 1000, 500);
        throw new Exception("Collinear anchors were accepted");
    }
    catch (CadFault fault) when (fault.Code == "IMAGE_CONTROL_POINTS_COLLINEAR") { }
    return Task.CompletedTask;
});
await Test("reject ambiguous and invalid edit plans before mutation", async () =>
{
    foreach (var json in new[] {
        "[]", "[{\"op\":\"unlisted\"}]", "[{\"op\":\"circle\",\"center\":[0,0],\"radius\":-1}]",
        "[{\"op\":\"line\",\"start\":[0],\"end\":[1,2]}]",
        "[{\"op\":\"line\",\"start\":[0,0],\"end\":[1,2],\"unexpected\":true}]",
        "[{\"op\":\"polyline\",\"points\":[[0,0,0],[1,1,2]]}]",
        "[{\"op\":\"move\",\"target\":\"future\",\"displacement\":[0,1]}]",
        "[{\"op\":\"move\",\"handle\":\"ZZ\",\"displacement\":[0,1]}]",
        "[{\"op\":\"line\",\"start\":[0,0],\"end\":[1,1],\"start\":[2,2]}]",
        "[{\"op\":\"block\",\"name\":\"A\",\"position\":[0,0],\"scale\":[1,1]}]",
        "[{\"op\":\"line\",\"id\":\"x\",\"start\":[0,0],\"end\":[1,1]},{\"op\":\"circle\",\"id\":\"x\",\"center\":[0,0],\"radius\":1}]"
    }) await Throws<CadFault>(() => Task.FromResult(EditPlan.Parse(json)));
});
await Test("mutation journal replay, conflicts and durable retention beyond 128 edits", async () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "cadmcp-journal-" + Guid.NewGuid().ToString("N"));
    var journal = new OperationJournal(directory);
    var request = new Request("transport1", "cad_edit", "session", "document", 7, Wire.Element(new { operation_id = "op1", operations_json = "[]" }));
    Assert(journal.Begin("op1", request) is null, "Unexpected replay");
    journal.Running("op1");
    Assert(journal.Find("op1")!.State == "running", "Lost pending status");
    journal.Complete("op1", new("transport1", "completed", new { handle = "A" }, "session", "document", 8));
    Assert(journal.Begin("op1", request with { RequestId = "transport2", Deadline = DateTimeOffset.UtcNow.AddMinutes(1) })!.Result!.Revision == 8, "Transport retry did not replay");
    await Throws<CadFault>(() => Task.FromResult(journal.Begin("op1", request with { ExpectedRevision = 8 })));
    await Throws<CadFault>(() => Task.FromResult(journal.Begin("op1", request with { DocumentId = "other" })));
    await Throws<CadFault>(() => Task.FromResult(journal.Begin("bad\"id", request)));
    for (int i = 1; i < 400; i++)
    { journal.Begin("key" + i, request); journal.Complete("key" + i, new("t", "completed", new { }, "session", "document", 8)); }
    journal.Begin("pending", request); journal.Running("pending");
    Assert(journal.Count == 401, "Journal lost entries");
    journal = new OperationJournal(directory);
    Assert(journal.Find("pending")!.State == "running", "Restart lost pending receipt");
    Assert(journal.Find("op1")!.Result is not null, "Record evicted; replay unsafe");
    File.WriteAllText(Path.Combine(directory, "key1.json"), "broken");
    await Throws<CadFault>(() => Task.FromResult(journal.Find("key1")));
    // A completed database edit must remain completed even if persisting its receipt fails.
    journal.Begin("write_failure", request); journal.Running("write_failure");
    Directory.CreateDirectory(Path.Combine(directory, "write_failure.json.tmp"));
    var receipt = journal.Complete("write_failure", new("t", "completed", new { transaction = "committed" }, "session", "document", 8));
    Assert(receipt.Error is null && Wire.Element(receipt.Data!).Text("journal_warning") is not null && journal.Find("write_failure")!.State == "completed", "Committed edit was misreported");
});
await Test("large-result archive paging, document isolation and retention", async () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "cadmcp-results-" + Guid.NewGuid().ToString("N"));
    var archive = new ResultArchive(directory);
    string id = archive.Put("s", "d", 12, new { text = "Сеть ✓" });
    var first = Wire.Element(archive.Read(id, "s", "d", 0, 8));
    Assert(first.GetProperty("historical").GetBoolean() && first.GetProperty("captured_revision").GetInt64() == 12 && first.GetProperty("next_offset").GetInt32() == 8, "Archive page metadata incorrect");
    await Throws<CadFault>(() => Task.FromResult(archive.Read(id, "s", "other", 0, 8)));
    await Throws<CadFault>(() => Task.FromResult(archive.Read("../x", "s", "d", 0, 8)));
    for (int i = 0; i < 70; i++) archive.Put("s", "d", 12, new { index = i });
    Assert(Directory.GetFiles(directory).Length <= 64, "Archive retention grew unbounded");
});
await Test("object cards parse tool results and preserve document identity", () =>
{
    var cards = CadCards.Parse("{\"session_id\":\"s\",\"document_id\":\"d\",\"data\":{\"entities\":[{\"handle\":\"AB\",\"type\":\"Line\"}]}}");
    Assert(cards.Count == 1 && cards[0].Session == "s" && cards[0].Document == "d" && cards[0].Handle == "AB", "Card lost drawing identity");
    return Task.CompletedTask;
});
await Test("read caches separate revisions, documents, spaces and limits", () =>
{
    var cache = new RevisionCache<string>(2); cache.Put("d1", "catalog", 3, "old");
    Assert(cache.TryGet("d1", "catalog", 3, out var value) && value == "old", "Cache miss");
    Assert(!cache.TryGet("d1", "catalog", 4, out _) && !cache.TryGet("d2", "catalog", 3, out _), "Stale cache returned");
    cache.Put("d1", "catalog", 4, "new"); Assert(!cache.TryGet("d1", "catalog", 3, out _), "Old revision survived replacement");
    var store = new SnapshotStore(); var s = store.Add("d", 1, [], false, "model", 100);
    Assert(store.Reuse("d", "model", 1, 100)?.Id == s.Id, "Snapshot not reused");
    Assert(store.Reuse("d", "paper", 1, 100) is null && store.Reuse("d", "model", 2, 100) is null && store.Reuse("d", "model", 1, 200) is null, "Snapshot mixed contexts");
    return Task.CompletedTask;
});
await Test("AutoLISP wrapper is balanced, isolates variables and prevents id injection", async () =>
{
    var script = LispScript.Wrap("operation_123"); int depth = 0; bool quoted = false, escaped = false;
    foreach (char c in script)
    {
        if (quoted) { if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') quoted = false; continue; }
        if (c == '"') quoted = true; else if (c == '(') depth++; else if (c == ')') depth--;
        Assert(depth >= 0, "LISP closes an unopened expression");
    }
    Assert(depth == 0 && !quoted && script.Contains("cadmcpbegin") && script.Contains("cadmcpfinish") && script.Contains("/ code value"), "Broken LISP evaluator");
    await Throws<ArgumentException>(() => Task.FromResult(LispScript.Wrap("x\") (erase)")));
});
await Test("chat history persists session and survives a torn newer file", () =>
{
    var root = Path.GetFullPath(Path.Combine(".runtime", "test-state-" + Guid.NewGuid().ToString("N")));
    var store = new ChatStateStore(root); var state = new ChatState(0, "codex.exe", "claude.exe", "host.exe", "CAD", "retained-session", "Сеть ✓", "matching-key");
    store.Save(state); Assert(store.Load() == state, "Session/Unicode not retained");
    var torn = Path.Combine(root, "chat-torn.json"); File.WriteAllText(torn, "{partial"); File.SetLastWriteTimeUtc(torn, DateTime.UtcNow.AddMinutes(1));
    Assert(store.Load() == state, "Torn file hid valid history");
    Assert(!Directory.EnumerateFiles(root, "*.tmp").Any(), "Temporary file left behind");
    return Task.CompletedTask;
});
await Test("nested Codex subscription errors are readable", () =>
{
    var error = JsonSerializer.SerializeToElement(new { error = new { message = JsonSerializer.Serialize(new { error = new { message = "Model is unavailable for this account" } }) } });
    Assert(CodexProvider.ErrorMessage(error) == "Model is unavailable for this account", "Nested error still raw JSON");
    Assert(CodexProvider.FriendlyError("Selected model is at capacity. Please try a different model.").Contains("Выберите другую модель"), "Capacity error is not actionable");
    return Task.CompletedTask;
});
var options = new ProviderOptions(Environment.ProcessPath!, @"C:\CAD test\host.exe", Environment.CurrentDirectory);
await Test("dropped files become bounded text and image inputs", () =>
{
    var root = Path.GetFullPath(Path.Combine(".runtime", "test-attachments-" + Guid.NewGuid().ToString("N")));
    Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root, "plan.txt"), "Чертёж ✓");
    File.WriteAllBytes(Path.Combine(root, "view.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6wAAAABJRU5ErkJggg=="));
    File.WriteAllBytes(Path.Combine(root, "base.dwg"), [1, 2, 3]);
    var files = ChatAttachments.Capture(new[] { "plan.txt", "view.png", "base.dwg" }.Select(name => ChatAttachments.Inspect(Path.Combine(root, name))).ToArray());
    Assert(files.Count == 3 && files[0].Text == "Чертёж ✓" && files[1].Kind == AttachmentKind.Image && files[2].Kind == AttachmentKind.File, "Wrong attachment types/content");
    Assert(ChatAttachments.AddToPrompt("Review", files).Contains(JsonSerializer.Serialize(Path.Combine(root, "base.dwg"))), "Binary file path lost");
    File.WriteAllText(Path.Combine(root, "bad.png"), "not an image");
    try { ChatAttachments.Inspect(Path.Combine(root, "bad.png")); throw new Exception("Invalid image accepted"); }
    catch (InvalidDataException) { }
    return Task.CompletedTask;
});
await Test("Codex and Claude receive text and images on their actual wire formats", async () =>
{
    var root = Path.GetFullPath(Path.Combine(".runtime", "test-provider-files-" + Guid.NewGuid().ToString("N")));
    Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root, "plan.txt"), "Чертёж ✓");
    File.WriteAllBytes(Path.Combine(root, "view.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6wAAAABJRU5ErkJggg=="));
    var files = ChatAttachments.Capture(new[] { "plan.txt", "view.png" }.Select(name => ChatAttachments.Inspect(Path.Combine(root, name))).ToArray());
    foreach (IChatProvider provider in new IChatProvider[] { new CodexProvider(options), new ClaudeProvider(options) })
    {
        var events = new List<ChatEvent>();
        await foreach (var item in provider.SendAsync("ATTACHMENT", default, files)) events.Add(item);
        Assert(events.Any(item => item.Kind == "completed"), "Attachment turn did not complete");
    }
});
await Test("model discovery paginates and excludes hidden entries without starting a turn", async () =>
{
    var models = await CodexCatalog.ReadAsync(options, default);
    Assert(models.Count == 2 && models.All(m => m.Id != "hidden-model"), "Wrong picker models");
    var selected = CodexCatalog.Select(models, "alternative-model", "high");
    Assert(selected.Model.Id == "alternative-model" && selected.Effort == "high", "Explicit selection lost");
    Assert(CodexCatalog.Select(models, null, null).Model.Id == "available-model", "Default changed");
    await Throws<IOException>(() => Task.FromResult(CodexCatalog.Select(models, "hidden-model", null)));
    await Throws<IOException>(() => Task.FromResult(CodexCatalog.Select(models, "alternative-model", "ultra")));
});
await Test("requested model and effort reach new and resumed Codex turns", async () =>
{
    var provider = new CodexProvider(options with { Model = "alternative-model", ReasoningEffort = "high" });
    foreach (int turn in new[] { 1, 2 })
    {
        var events = new List<ChatEvent>();
        await foreach (var e in provider.SendAsync("SELECT", default)) events.Add(e);
        Assert(events.Any(e => e.Kind == "model" && e.Text == "alternative-model") && events.Any(e => e.Kind == "effort" && e.Text == "high"), "Wrong selected model/effort");
        Assert(provider.SessionId == "test-thread", "Model switch lost conversation");
    }
    var switched = new CodexProvider(options) { SessionId = provider.SessionId };
    await foreach (var _ in switched.SendAsync("Read", default)) { }
    Assert(switched.SessionId == provider.SessionId, "Changed model created another session");
});
await Test("Codex retries an active writer and releases the provider process", async () =>
{
    var provider = new CodexProvider(options) { SessionId = "retry-thread" };
    var events = new List<ChatEvent>();
    await foreach (var e in provider.SendAsync("Read", default)) events.Add(e);
    Assert(events.Any(e => e.Kind == "completed") && provider.SessionId == "test-thread", "Active writer prevented a recovered turn");
});
await Test("unavailable model fails instead of silently selecting a different one", async () =>
{
    var provider = new CodexProvider(options with { Model = "missing-model" });
    await Throws<IOException>(async () => { await foreach (var _ in provider.SendAsync("Read", default)) { } });
    Assert(provider.SessionId is null, "Started thread with unavailable model");
});
await Test("model settings preserve history and migrate only the standard Codex path", () =>
{
    var root = Path.GetFullPath(Path.Combine(".runtime", "test-model-state-" + Guid.NewGuid().ToString("N")));
    var store = new ChatStateStore(root);
    var state = new ChatState(0, "desktop.exe", "claude.exe", "host.exe", "CAD", "retained-session", "Сеть ✓", "0|desktop.exe|host.exe|CAD", "alternative-model", "high");
    store.Save(state); Assert(store.Load() == state, "Model settings not retained");
    var migrated = ChatStateStore.UseBundledCodex(state, "bundled.exe", "desktop.exe");
    Assert(migrated.SessionId == state.SessionId && migrated.AdapterKey == "0|bundled.exe|host.exe|CAD" && migrated.CodexModel == state.CodexModel, "Upgrade discarded conversation");
    Assert(ChatStateStore.UseBundledCodex(state with { CodexExecutable = "custom.exe" }, "bundled.exe", "desktop.exe").CodexExecutable == "custom.exe", "Custom CLI overwritten");
    var legacy = JsonSerializer.Deserialize<ChatState>("""{"Provider":0,"CodexExecutable":"codex.exe","ClaudeExecutable":"claude.exe","Host":"host.exe","Directory":"CAD","SessionId":"legacy","Transcript":"old"}""")!;
    Assert(ChatStateStore.Valid(legacy) && legacy.CodexModel is null && legacy.CodexReasoningEffort is null, "Old history incompatible");
    return Task.CompletedTask;
});
foreach (var name in new[] { "Codex", "Claude" })
{
    IChatProvider Make() => name == "Codex" ? new CodexProvider(options) : new ClaudeProvider(options);
    await Test(name + " stream/session/Unicode", async () =>
    {
        var provider = Make(); var events = new List<ChatEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await foreach (var e in provider.SendAsync("Read", timeout.Token)) events.Add(e);
        Assert(string.Concat(events.Where(e => e.Kind == "text").Select(e => e.Text)) == "Сеть ✓", "Lost/duplicated text");
        Assert(provider.SessionId is not null && events.Any(e => e.Kind == "completed"), "Session missing");
        if (name == "Codex")
        {
            Assert(events.Any(e => e.Kind == "model" && e.Text == "available-model"), "Catalog selection missing");
            Assert(events.Any(e => e.Kind == "reasoning_summary" && e.Text.Contains("размеры")), "Reasoning summary missing");
            Assert(events.Count(e => e.Kind == "step" && e.Text.Contains("cad_search")) == 2, "Tool progress missing");
            Assert(events.Any(e => e.Kind == "status" && e.Text.Contains("Помощники")), "Subagent progress missing");
        }
        else Assert(events.Any(e => e.Kind == "step" && e.Text == "Завершено: mcp__cad__cad_edit"), "Claude edit completion missing");
        events.Clear();
        await foreach (var e in provider.SendAsync("Resume", timeout.Token)) events.Add(e);
        Assert(events.Any(e => e.Kind == "completed"), "Resume failed");
    });
    await Test(name + " cancel silent process", async () =>
    {
        var provider = Make(); using var timeout = new CancellationTokenSource(400); var sw = Stopwatch.StartNew();
        await Throws<OperationCanceledException>(async () => { await foreach (var e in provider.SendAsync("SILENT", timeout.Token)) { } });
        Assert(sw.Elapsed < TimeSpan.FromSeconds(4), "Stop hung waiting for stdout");
    });
}
foreach(var name in new[]{"Codex","Claude"})
{
    await Test(name+" active task accepts follow-up without a second writer",async()=>
    {
        IChatProvider live=name=="Codex"?new CodexProvider(options):new ClaudeProvider(options);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert((await live.SteerAsync(new("before",Array.Empty<ChatAttachment>()),timeout.Token)).State=="unavailable","Idle provider pretended to receive input");
        Task<InputReceipt>? receipt=null;var text=new System.Text.StringBuilder();
        await foreach(var e in live.SendAsync("STEER",timeout.Token))
        {if(e.Kind=="input_ready"&&receipt is null)receipt=live.SteerAsync(new("Уточнение ✓",Array.Empty<ChatAttachment>()),timeout.Token);if(e.Kind=="text")text.Append(e.Text);}
        Assert(receipt is not null&&(await receipt).State=="accepted"&&text.ToString().Contains("Учёл уточнение"),"Follow-up not acknowledged or result lost");
        Assert((await live.SteerAsync(new("after",Array.Empty<ChatAttachment>()),timeout.Token)).State=="unavailable","Closed turn accepted input");
    });
}
await Test("rejected Codex steering keeps the original response",async()=>
{
    var live=new CodexProvider(options);Task<InputReceipt>? receipt=null;bool completed=false;
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await foreach(var e in live.SendAsync("STEER_REJECT",timeout.Token))
    {if(e.Kind=="input_ready"&&receipt is null)receipt=live.SteerAsync(new("Уточнение ✓",Array.Empty<ChatAttachment>()),timeout.Token);completed|=e.Kind=="completed";}
    Assert(completed&&receipt is not null&&(await receipt).State=="rejected","Input rejection destroyed current task");
});
await Test("drafting contract validates formulas, bounded cells and verified form widths",async()=>
{
    EditPlan.Parse("""[{"op":"table_create","id":"a","position":[0,0],"rows":2,"columns":2,"cells":[{"cell":"A1","value":3}]},{"op":"table_create","position":[100,0],"rows":2,"columns":2,"cells":[{"cell":"A1","formula":"={{n}}*2","references":[{"name":"n","table_target":"a","cell":"A1"}]}]}]""");
    await Throws<CadFault>(()=>Task.FromResult(EditPlan.Parse("""[{"op":"table_create","position":[0,0],"rows":2,"columns":2,"cells":[{"cell":"C1","value":1}]}]""")));
    await Throws<CadFault>(()=>Task.FromResult(EditPlan.Parse("""[{"op":"table_create","position":[0,0],"rows":2,"columns":2,"merges":[[0,0,3,1]]}]""")));
    await Throws<CadFault>(()=>Task.FromResult(EditPlan.Parse("""[{"op":"table_cells","handle":"AB","cells":[{"cell":"A1","formula":"={{missing}}*2"}]}]""")));
    foreach(var form in SpdsTemplates.Tables.Where(f=>f.VerifiedForm))Assert(form.Widths.Sum()==185&&form.HeaderHeight==15&&form.RowHeight>=8,"Wrong verified GOST form dimensions");
    Assert(DraftingPlan.Address("$AA$12")== (11,26)&&DraftingPlan.Address(11,26)=="AA12","A1 address conversion failed");
});
await Test("provider arguments preserve paths and isolate Claude MCP config", () =>
{
    var a = new ClaudeProvider(options).Arguments();
    var config = JsonDocument.Parse(a[Array.IndexOf(a, "--mcp-config") + 1]);
    Assert(config.RootElement.GetProperty("mcpServers").GetProperty("cad").GetProperty("command").GetString() == options.McpExecutable, "Path escaped incorrectly");
    Assert(a.Contains("--strict-mcp-config"), "Claude config not isolated");
    Assert(a.Contains("Agent(cad_researcher)") && a.Contains("--agents") && a[Array.IndexOf(a,"--agent")+1]=="cad_primary", "Claude helper types must be restricted by the primary role");
    using var helper = JsonDocument.Parse(a[Array.IndexOf(a, "--agents") + 1]);
    var helperTools = helper.RootElement.GetProperty("cad_researcher").GetProperty("tools").EnumerateArray().Select(x => x.GetString()).ToArray();
    Assert(helper.RootElement.GetProperty("cad_researcher").GetProperty("mcpServers")[0].GetProperty("cad").GetProperty("env").Text("CAD_MCP_READ_ONLY")=="1","Claude helper needs server-enforced read-only access");
    Assert(helperTools.Contains("mcp__cad__cad_search") && !helperTools.Contains("mcp__cad__cad_edit") &&
        !helperTools.Contains("mcp__cad__cad_lisp"), "Claude helper must be read-only");
    var single = new ClaudeProvider(options with { MaxSubagents = 0 }).Arguments();
    Assert(single.Contains("--disallowedTools") && !single.Contains("--agents"), "Claude helper opt-out missing");
    var selected = new ClaudeProvider(options with { Model = "opus", ReasoningEffort = "high" }).Arguments();
    Assert(selected[Array.IndexOf(selected, "--model") + 1] == "opus" &&
        selected[Array.IndexOf(selected, "--effort") + 1] == "high", "Claude model or effort selection missing");
    return Task.CompletedTask;
});
await Test("Owned CAD cancellation isolates chats and documents",()=>
{
    using var control=new OperationControl();var data=Wire.Element(new{});
    var a=new Request("a","cad_edit","s","d1",Data:data,OwnerId:"chat1");var b=a with{RequestId="b",OwnerId="chat2"};var c=a with{RequestId="c",DocumentId="d2"};
    var ta=control.Accept("a",a);var tb=control.Accept("b",b);var tc=control.Accept("c",c);
    var stopped=control.Cancel(a with{Operation="cad_cancel"});Assert(stopped.SequenceEqual(new[]{"a"})&&ta.IsCancellationRequested&&!tb.IsCancellationRequested&&!tc.IsCancellationRequested,"Stop leaked into another chat/document");
    control.Complete("a");Assert(!Wire.Element(control.Snapshot()).EnumerateArray().Any(x=>x.Text("operation_id")=="a"),"Completed job retained");return Task.CompletedTask;
});
await Test("Word exports editable engineering equations and tables",()=>
{
    string path=Environment.GetEnvironmentVariable("CADMCP_DOCX_FIXTURE")??Path.Combine(Path.GetTempPath(),"cad-native-math-"+Guid.NewGuid().ToString("N")+".docx");
    ChatWordExporter.Save(path,[new ChatLine("user","Проверь прогиб и напряжение балки"),new ChatLine("assistant","## Проверка балки\n\nНапряжение $\\sigma=\\frac{M}{W}$ сравниваем с допускаемым.\n\n$$ f=\\frac{5qL^4}{384EI} \\leq \\frac{L}{250} $$\n\n$$ r=\\sqrt[3]{x^2+y^2},\\quad A=\\begin{bmatrix}1&2\\\\3&4\\end{bmatrix} $$\n\n| Параметр | Значение |\n|---|---|\n| Пролёт | **5000 мм** |\n| Формула | $A=ab$ |")]);
    using var doc=DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(path,false);
    var errors=new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(doc).ToArray();Assert(errors.Length==0,string.Join(";",errors.Select(e=>e.Description)));
    var xml=doc.MainDocumentPart!.Document.OuterXml;Assert(xml.Contains("m:f")&&xml.Contains("m:rad")&&xml.Contains("m:m")&&xml.Contains("m:sSup")&&!xml.Contains("\\frac"),"Native OMML missing");
    Assert(doc.MainDocumentPart.Document.Body!.Elements<DocumentFormat.OpenXml.Wordprocessing.Table>().Count()==1,"Word table missing");return Task.CompletedTask;
});
await Test("Codex grants CAD tools without global permission changes", () =>
{
    var arguments = new CodexProvider(options).Arguments();
    Assert(arguments.Contains("mcp_servers.cad.enabled=true") &&
        arguments.Contains("mcp_servers.cad.default_tools_approval_mode=\"approve\""), "CAD server permission missing");
    Assert(!arguments.Any(a => a.Contains("danger-full-access") || a.Contains("bypass-approvals") || a.StartsWith("apps.")), "Global permissions changed");
    var command = arguments.Single(a => a.StartsWith("mcp_servers.cad.command="));
    Assert(JsonSerializer.Deserialize<string>(command["mcp_servers.cad.command=".Length..]) == options.McpExecutable, "CAD host path corrupted");
    Assert(arguments.Contains("agents.enabled=true") && arguments.Contains("agents.max_concurrent_threads_per_session=3"), "Subagent limit missing");
    var withoutAgents = new CodexProvider(options with { MaxSubagents = 0 }).Arguments();
    Assert(withoutAgents.Contains("agents.enabled=false"), "Subagent opt-out missing");
    return Task.CompletedTask;
});
await Test("MCP initialize/list/call/image over actual stdio SDK", async () =>
{
    string pipe = "cadmcp-mcp-test-" + Guid.NewGuid().ToString("N");
    const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6wAAAABJRU5ErkJggg==";
    using var fakeBroker = new PipeServer(pipe, (r, ct) => Task.FromResult(r.Operation == "cad_render"
        ? new Response(r.RequestId, "partial", new { image_base64 = png, source = "fixture" })
        : new Response(r.RequestId, "completed", new { fixture = true, operation = r.Operation, payload = r.Data })));
    fakeBroker.Start();
    var hostPath = Path.GetFullPath("src/CadMcp.Host/bin/Release/net8.0-windows/CadMcp.Host.exe");
    var info = ProviderProcess.StartInfo(new(hostPath, "unused", Environment.CurrentDirectory), ["--broker-pipe", pipe]);
    using var process = Process.Start(info)!; var stderr = ProviderProcess.DrainErrors(process);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    async Task<JsonElement> Rpc(object body, int id)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(body)); await process.StandardInput.FlushAsync();
        while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
        {
            var reply = JsonDocument.Parse(line).RootElement.Clone();
            if (reply.TryGetProperty("id", out var rid) && rid.GetInt32() == id)
            { Assert(!reply.TryGetProperty("error", out _), reply.ToString()); return reply.GetProperty("result").Clone(); }
        }
        throw new Exception("MCP stdout closed");
    }
    try
    {
        await Rpc(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "test", version = "1" } } }, 1);
        await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"); await process.StandardInput.FlushAsync();
        var list = await Rpc(new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } }, 2);
        var names = list.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
        Assert(names.Length == 38 && names.Contains("cad_steel_catalog") && names.Contains("cad_search") && names.Contains("cad_result_get") && names.Contains("cad_edit") &&
            names.Contains("cad_export") && names.Contains("cad_publish") && names.Contains("cad_lisp") &&
            names.Contains("cad_operation_status") && names.Contains("cad_render") && names.Contains("cad_image_register") &&
            names.Contains("cad_image_point") && names.Contains("cad_vertical_catalog") && names.Contains("cad_vertical_get") &&
            names.Contains("cad_verify") && names.Contains("cad_operation_list") && names.Contains("cad_reference_calibrate") &&
            names.Contains("cad_reference_point") && names.Contains("cad_reference_compare"), "Wrong tools");
        var call = await Rpc(new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "cad_sessions", arguments = new { } } }, 3);
        Assert(call.GetProperty("content")[0].GetProperty("text").GetString()!.Contains("fixture"), "Tool did not reach broker");
        var render = await Rpc(new { jsonrpc = "2.0", id = 4, method = "tools/call", @params = new { name = "cad_render", arguments = new { session_id = "s", document_id = "d", expected_revision = 1 } } }, 4);
        Assert(render.GetProperty("content")[1].GetProperty("type").GetString() == "image", "Image not sent as MCP image");
        Assert(Convert.FromBase64String(render.GetProperty("content")[1].GetProperty("data").GetString()!).SequenceEqual(Convert.FromBase64String(png)), "Image bytes changed");
        Assert(!render.GetProperty("content")[0].GetProperty("text").GetString()!.Contains(png), "Base64 duplicated in text");
        var help = await Rpc(new { jsonrpc = "2.0", id = 5, method = "tools/call", @params = new { name = "cad_edit_help", arguments = new { } } }, 5);
        Assert(help.GetProperty("content")[0].GetProperty("text").GetString()!.Contains("dimension_aligned"), "Missing edit contract");
        var edit = await Rpc(new { jsonrpc = "2.0", id = 6, method = "tools/call", @params = new { name = "cad_edit", arguments = new { session_id = "s", document_id = "d", expected_revision = 1, operation_id = "change1", operations_json = "[{\"op\":\"text\",\"text\":\"Сеть ✓\"}]" } } }, 6);
        using var editBody = JsonDocument.Parse(edit.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert(editBody.RootElement.GetProperty("data").GetProperty("operation").GetString() == "cad_edit" && editBody.RootElement.GetProperty("data").GetProperty("payload").GetProperty("operation_id").GetString() == "change1", "Edit did not reach broker intact");
        var status = await Rpc(new { jsonrpc = "2.0", id = 7, method = "tools/call", @params = new { name = "cad_operation_status", arguments = new { session_id = "s", document_id = "d", operation_id = "change1" } } }, 7);
        Assert(status.GetProperty("content")[0].GetProperty("text").GetString()!.Contains("cad_operation_status"), "Status not routed");
    }
    finally { ProviderProcess.Stop(process); await stderr; }
});
await Test("MCP enforces project pin for read edit render and review helpers",async()=>
{
    string pipe="cadmcp-pin-test-"+Guid.NewGuid().ToString("N");var requests=new System.Collections.Concurrent.ConcurrentQueue<Request>();
    using var broker=new PipeServer(pipe,(r,ct)=>{requests.Enqueue(r);return Task.FromResult(new Response(r.RequestId,"completed",new{document_id=r.DocumentId,owner_id=r.OwnerId}));});broker.Start();
    foreach(bool helper in new[]{false,true})
    {
        var info=ProviderProcess.StartInfo(new(Path.GetFullPath("src/CadMcp.Host/bin/Release/net8.0-windows/CadMcp.Host.exe"),"unused",Environment.CurrentDirectory),["--broker-pipe",pipe]);
        info.Environment["CAD_MCP_SESSION_ID"]="s";info.Environment["CAD_MCP_DOCUMENT_ID"]="d";info.Environment["CAD_MCP_OWNER_ID"]="chat-a";if(helper)info.Environment["CAD_MCP_READ_ONLY"]="1";
        using var process=Process.Start(info)!;var stderr=ProviderProcess.DrainErrors(process);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        async Task<JsonElement> Rpc(int id,string method,object parameters)
        {await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{jsonrpc="2.0",id,method,@params=parameters}));await process.StandardInput.FlushAsync();while(await process.StandardOutput.ReadLineAsync(timeout.Token) is {} line){var reply=JsonDocument.Parse(line).RootElement;if(reply.TryGetProperty("id",out var rid)&&rid.GetInt32()==id)return reply.GetProperty("result").Clone();}throw new IOException("MCP fixture stdout closed");}
        try
        {
            await Rpc(1,"initialize",new{protocolVersion="2025-11-25",capabilities=new{},clientInfo=new{name="pin-test",version="1"}});
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");await process.StandardInput.FlushAsync();
            var context=await Rpc(2,"tools/call",new{name="cad_context",arguments=new{session_id="s"}});Assert(!context.TryGetProperty("isError",out var er)||!er.GetBoolean(),"Pinned context rejected");
            Assert(requests.Last().DocumentId=="d"&&requests.Last().OwnerId=="chat-a","Scope/owner not forwarded");
            using(var contextBody=JsonDocument.Parse(context.GetProperty("content")[0].GetProperty("text").GetString()!))Assert(contextBody.RootElement.GetProperty("data").GetProperty("access").GetProperty("read_only").GetBoolean()==helper,"Context must report actual enforced helper access");
            foreach(var(name,arguments) in new (string,object)[]{("cad_context",new{session_id="s",document_id="foreign"}),("cad_render",new{session_id="s",document_id="foreign",expected_revision=0}),("cad_edit",new{session_id="s",document_id=helper?"d":"foreign",expected_revision=0,operation_id="pin",operations_json="[{\"op\":\"line\",\"start\":[0,0],\"end\":[1,1]}]"})})
            {int before=requests.Count;var reply=await Rpc(before+10,"tools/call",new{name,arguments});Assert(reply.GetProperty("isError").GetBoolean()&&requests.Count==before,"Foreign DWG or helper mutation reached CAD broker: "+name);}
        }
        finally{process.StandardInput.Close();await ProviderProcess.FinishAsync(process,stderr);}
    }
});
await Test("inherited Codex MCP connections enforce primary thread metadata and fail closed",async()=>
{
    string pipe="cad-meta-test-"+Guid.NewGuid().ToString("N"),binding=Path.Combine(Path.GetTempPath(),"cad-primary-"+Guid.NewGuid().ToString("N")+".txt");File.WriteAllText(binding,"primary");int calls=0;
    using var broker=new PipeServer(pipe,(r,ct)=>{Interlocked.Increment(ref calls);return Task.FromResult(new Response(r.RequestId,"completed",new{ok=true}));});broker.Start();
    var info=ProviderProcess.StartInfo(new(Path.GetFullPath("src/CadMcp.Host/bin/Release/net8.0-windows/CadMcp.Host.exe"),"unused",Environment.CurrentDirectory),["--broker-pipe",pipe]);info.Environment["CAD_MCP_PRIMARY_THREAD_FILE"]=binding;
    using var process=Process.Start(info)!;var errors=ProviderProcess.DrainErrors(process);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
    async Task<JsonElement> Rpc(int id,string method,object parameters)
    {await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{jsonrpc="2.0",id,method,@params=parameters}));await process.StandardInput.FlushAsync();while(await process.StandardOutput.ReadLineAsync(timeout.Token) is {} line){var reply=JsonDocument.Parse(line).RootElement;if(reply.TryGetProperty("id",out var rid)&&rid.GetInt32()==id)return reply.GetProperty("result").Clone();}throw new IOException("Closed fixture");}
    try{
        await Rpc(1,"initialize",new{protocolVersion="2025-11-25",capabilities=new{},clientInfo=new{name="metadata-test",version="1"}});await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");await process.StandardInput.FlushAsync();
        int id=2;foreach(string? thread in new string?[]{"primary","child",null})
        {
            var result=await Rpc(id++,"tools/call",new{name="cad_edit",arguments=new{session_id="s",document_id="d",expected_revision=0,operation_id="fake",operations_json="[{\"op\":\"line\",\"start\":[0,0],\"end\":[1,1]}]"},_meta=thread is null?null:new{threadId=thread}});
            bool failed=result.TryGetProperty("isError",out var er)&&er.GetBoolean();Assert(failed==(thread!="primary"),"MCP caller identity was not enforced");
        }
        Assert(calls==1,"Child or unidentified caller reached the CAD broker");
        File.Delete(binding);var blocked=await Rpc(id,"tools/call",new{name="cad_focus",arguments=new{session_id="s",document_id="d",expected_revision=0,handle="A"},_meta=new{threadId="primary"}});Assert(blocked.GetProperty("isError").GetBoolean()&&calls==1,"Missing primary binding opened write access");
    }finally{process.StandardInput.Close();await ProviderProcess.FinishAsync(process,errors);File.Delete(binding);}
});
await Test("broker automatic startup, reuse and shutdown", async () =>
{
    var pipe = "cadmcp-bootstrap-test-" + Guid.NewGuid().ToString("N");
    var hostPath = Path.GetFullPath("src/CadMcp.Host/bin/Release/net8.0-windows/CadMcp.Host.exe");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    try
    {
        await BrokerBootstrap.EnsureAsync(hostPath, timeout.Token, pipe);
        await BrokerBootstrap.EnsureAsync(hostPath, timeout.Token, pipe);
        var reply = await PipeClient.CallAsync(pipe, new("test-bootstrap", "broker_ping"), timeout.Token);
        Assert(reply.Status == "completed", "Broker not ready");
    }
    finally
    {
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await PipeClient.CallAsync(pipe, new("test-stop", "broker_stop"), stopTimeout.Token);
    }
    await Task.Delay(600);
    using var stoppedTimeout = new CancellationTokenSource(300);
    await Throws<OperationCanceledException>(() => PipeClient.CallAsync(pipe, new("after-stop", "broker_ping"), stoppedTimeout.Token));
});
Console.WriteLine($"Completed: {passed} tests. No AutoCAD or real provider process started.");
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL: " + error);
    Environment.ExitCode = 1;
}
