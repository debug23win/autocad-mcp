using System.Text.Json;
using CadMcp.Core;

namespace CadMcp.Tests;

public sealed class JournalTests
{
    private static Request Edit(string session = "s") => new("r", "cad_edit", session, "d", 1, Wire.Element(new { operation_id = "one", operations_json = "[]" }));

    [Fact]
    public async Task Lost_mutation_response_retrieves_the_receipt_without_resending_the_edit()
    {
        var request = Edit(); int edits = 0, statuses = 0;
        async Task<Response> Send(Request r, CancellationToken ct)
        {
            await Task.Yield();
            if (r.Operation == "cad_edit") { edits++; throw new IOException("response lost after commit"); }
            statuses++; return new(r.RequestId, "completed", new { state = "completed", result = new Response("r", "completed", new { transaction = "committed" }, "s", "d", 2) });
        }
        var result = await MutationRecovery.CallAsync(request, Send, default);
        Assert.True(edits == 1 && statuses == 1 && result.Revision == 2 && result.Error is null, "Recovery repeated mutation or lost confirmed receipt");
        var pending = await MutationRecovery.CallAsync(request, (r, ct) => r.Operation == "cad_edit" ? Task.FromException<Response>(new IOException("lost")) : Task.FromResult(new Response(r.RequestId, "completed", new { state = "running" })), default);
        Assert.Equal("pending", pending.Status);
        Assert.Equal("running", Wire.Element(pending.Data!).Text("state"));
        var unknown = await MutationRecovery.CallAsync(request, (r, ct) => Task.FromException<Response>(new IOException("unreachable")), default);
        Assert.Equal("OPERATION_UNCERTAIN", unknown.Error?.Code);
    }

    [Theory]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(InvalidDataException))]
    [InlineData(typeof(JsonException))]
    public async Task Every_transport_failure_starts_receipt_recovery(Type failure)
    {
        int statuses = 0;
        var result = await MutationRecovery.CallAsync(Edit(), (r, ct) =>
        {
            if (r.Operation == "cad_edit") throw (Exception)Activator.CreateInstance(failure, "transport")!;
            statuses++;
            return Task.FromResult(new Response(r.RequestId, "completed", new { state = "completed", result = new Response("r", "completed", new { transaction = "committed" }, "s", "d", 2) }));
        }, default);
        Assert.Equal(1, statuses);
        Assert.Equal(2, result.Revision);
    }

    [Fact]
    public async Task Concurrent_acceptance_is_unique_and_archived_receipts_survive_a_missing_worker()
    {
        using var root = new TempFolder(); string session = Guid.NewGuid().ToString("N");
        var journal = new OperationJournal(Path.Combine(root.Path, session)); var request = new Request("r", "cad_edit", session, "d", 1, Wire.Element(new { operation_id = "shared" }));
        var accepted = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() => journal.Begin("shared", request))));
        Assert.Equal(1, accepted.Count(e => e is null));
        journal.Running("shared");
        var broker = new Broker(Path.Combine(root.Path, "workers"), root.Path);
        var status = await broker.DispatchAsync(new("status", "cad_operation_status", session, "d", Data: Wire.Element(new { operation_id = "shared" })), default);
        Assert.Equal("unknown", Wire.Element(status.Data!).Text("state"));
        Assert.True(Wire.Element(status.Data!).GetProperty("historical").GetBoolean());
        journal.Complete("shared", new("r", "completed", new { transaction = "committed" }, session, "d", 2));
        status = await broker.DispatchAsync(new("status", "cad_operation_status", session, "d", Data: Wire.Element(new { operation_id = "shared" })), default);
        Assert.Equal("completed", Wire.Element(status.Data!).Text("state"));
    }

    [Fact]
    public void Replay_conflicts_and_durable_retention_beyond_128_edits()
    {
        using var directory = new TempFolder();
        var journal = new OperationJournal(directory.Path);
        var request = new Request("transport1", "cad_edit", "session", "document", 7, Wire.Element(new { operation_id = "op1", operations_json = "[]" }));
        Assert.Null(journal.Begin("op1", request));
        journal.Running("op1");
        Assert.Equal("running", journal.Find("op1")!.State);
        journal.Complete("op1", new("transport1", "completed", new { handle = "A" }, "session", "document", 8));
        Assert.Equal(8, journal.Begin("op1", request with { RequestId = "transport2", Deadline = DateTimeOffset.UtcNow.AddMinutes(1) })!.Result!.Revision);
        Assert.Throws<CadFault>(() => journal.Begin("op1", request with { ExpectedRevision = 8 }));
        Assert.Throws<CadFault>(() => journal.Begin("op1", request with { DocumentId = "other" }));
        Assert.Throws<CadFault>(() => journal.Begin("bad\"id", request));
        for (int i = 1; i < 400; i++)
        { journal.Begin("key" + i, request); journal.Complete("key" + i, new("t", "completed", new { }, "session", "document", 8)); }
        journal.Begin("pending", request); journal.Running("pending");
        Assert.Equal(401, journal.Count);
        journal = new OperationJournal(directory.Path);
        Assert.Equal("running", journal.Find("pending")!.State);
        Assert.NotNull(journal.Find("op1")!.Result);
        File.WriteAllText(directory.File("key1.json"), "broken");
        Assert.Throws<CadFault>(() => journal.Find("key1"));
        // A completed database edit must remain completed even if persisting its receipt fails.
        journal.Begin("write_failure", request); journal.Running("write_failure");
        Directory.CreateDirectory(directory.File("write_failure.json.tmp"));
        var receipt = journal.Complete("write_failure", new("t", "completed", new { transaction = "committed" }, "session", "document", 8));
        Assert.True(receipt.Error is null && Wire.Element(receipt.Data!).Text("journal_warning") is not null && journal.Find("write_failure")!.State == "completed", "Committed edit was misreported");
    }

    [Fact]
    public void Listing_skips_an_unreadable_receipt_and_reports_it()
    {
        using var directory = new TempFolder();
        var journal = new OperationJournal(directory.Path);
        var request = new Request("t", "cad_edit", "session", "document", 1, Wire.Element(new { operation_id = "x", operations_json = "[]" }), OwnerId: "chat");
        foreach (var id in new[] { "first", "second", "third" })
        { journal.Begin(id, request); journal.Complete(id, new("t", "completed", new { }, "session", "document", 2)); }
        journal = new OperationJournal(directory.Path);
        File.WriteAllText(directory.File("second.json"), "{torn");
        var unreadable = new List<string>();
        var recent = journal.Recent("document", DateTimeOffset.MinValue, 50, "chat", unreadable);
        Assert.Equal(new[] { "first", "third" }, recent.Select(r => r.Id).Order());
        Assert.Equal(new[] { "second" }, unreadable);
        Assert.Empty(journal.Recent("other-document", DateTimeOffset.MinValue));
        Assert.Single(journal.Recent("document", DateTimeOffset.MinValue, 1));
    }

    [Fact]
    public void Large_result_archive_pages_isolates_documents_and_is_bounded()
    {
        using var directory = new TempFolder();
        var archive = new ResultArchive(directory.Path);
        string id = archive.Put("s", "d", 12, new { text = "Сеть ✓" });
        var first = Wire.Element(archive.Read(id, "s", "d", 0, 8));
        Assert.True(first.GetProperty("historical").GetBoolean() && first.GetProperty("captured_revision").GetInt64() == 12 && first.GetProperty("next_offset").GetInt32() == 8);
        Assert.Throws<CadFault>(() => archive.Read(id, "s", "other", 0, 8));
        Assert.Throws<CadFault>(() => archive.Read("../x", "s", "d", 0, 8));
        for (int i = 0; i < 70; i++) archive.Put("s", "d", 12, new { index = i });
        Assert.True(Directory.GetFiles(directory.Path).Length <= 64, "Archive retention grew unbounded");
    }
}

public sealed class BrokerTests
{
    [Fact]
    public async Task Missing_session_is_rejected()
    {
        using var root = new TempFolder();
        var broker = new Broker(root.File("workers"));
        Assert.Empty(broker.Discover());
        await Assert.ThrowsAnyAsync<CadFault>(() => broker.DispatchAsync(new("r", "cad_snapshot"), default));
    }

    [Fact]
    public async Task Independent_cad_sessions_are_probed_in_parallel()
    {
        using var root = new TempFolder();
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
        File.WriteAllText(root.File("one.json"), JsonSerializer.Serialize(new WorkerDescriptor("s1", first, Environment.ProcessId, "test"), Wire.Json));
        File.WriteAllText(root.File("two.json"), JsonSerializer.Serialize(new WorkerDescriptor("s2", second, Environment.ProcessId, "test"), Wire.Json));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = await new Broker(root.Path).DispatchAsync(new("sessions", "cad_sessions"), timeout.Token);
        var sessions = Wire.Element(result.Data!);
        Assert.Equal(2, sessions.GetArrayLength());
        Assert.All(sessions.EnumerateArray(), x => Assert.True(x.GetProperty("reachable").GetBoolean()));
    }

    [Fact]
    public void Descriptors_of_exited_cad_processes_are_removed_and_the_newest_duplicate_wins()
    {
        using var root = new TempFolder();
        // The test command line prints its usage and exits at once.
        using var exited = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(TestEnvironment.FakeCli) { UseShellExecute = false, RedirectStandardError = true })!;
        exited.WaitForExit();
        File.WriteAllText(root.File("dead.json"), JsonSerializer.Serialize(new WorkerDescriptor("dead", "cadmcp-worker-dead", exited.Id, "test"), Wire.Json));
        File.WriteAllText(root.File("old.json"), JsonSerializer.Serialize(new WorkerDescriptor("same", "cadmcp-worker-old", Environment.ProcessId, "test"), Wire.Json));
        File.SetLastWriteTimeUtc(root.File("old.json"), System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().AddSeconds(1));
        File.WriteAllText(root.File("new.json"), JsonSerializer.Serialize(new WorkerDescriptor("same", "cadmcp-worker-new", Environment.ProcessId, "test"), Wire.Json));
        File.SetLastWriteTimeUtc(root.File("new.json"), DateTime.UtcNow.AddSeconds(1));
        var workers = new Broker(root.Path).Discover();
        Assert.Equal(new[] { "cadmcp-worker-new" }, workers.Select(w => w.PipeName));
        Assert.True(File.Exists(root.File("old.json")), "A live duplicate descriptor is not stale");
        Assert.False(File.Exists(root.File("dead.json")));
        // A reused process id: this process started after a descriptor written an hour ago.
        Assert.False(Broker.WorkerProcessAlive(Environment.ProcessId, DateTime.UtcNow.AddHours(-1)));
    }

    [Theory]
    [InlineData("0.10.0-preview", "0.10.1-preview", true)]
    [InlineData("0.10.1-preview", "0.10.1-preview", false)]
    [InlineData("0.11.0", "0.10.1-preview", false)]
    [InlineData("unknown", "0.10.1-preview", false)]
    public void Only_an_older_broker_is_replaced(string running, string current, bool older) =>
        Assert.Equal(older, BrokerBootstrap.IsOlder(running, current));

    [Fact]
    public async Task Broker_starts_automatically_is_reused_and_shuts_down()
    {
        var pipe = "cadmcp-bootstrap-test-" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await BrokerBootstrap.EnsureAsync(TestEnvironment.HostExecutable, timeout.Token, pipe);
            await BrokerBootstrap.EnsureAsync(TestEnvironment.HostExecutable, timeout.Token, pipe);
            var reply = await PipeClient.CallAsync(pipe, new("test-bootstrap", "broker_ping"), timeout.Token);
            Assert.Equal("completed", reply.Status);
            Assert.Equal(Wire.Version, Wire.Element(reply.Data!).Text("version"));
        }
        finally
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await PipeClient.CallAsync(pipe, new("test-stop", "broker_stop"), stopTimeout.Token);
        }
        await Task.Delay(600);
        using var stoppedTimeout = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PipeClient.CallAsync(pipe, new("after-stop", "broker_ping"), stoppedTimeout.Token));
    }

    [Fact]
    public async Task An_older_broker_is_stopped_and_replaced_by_this_release()
    {
        var pipe = "cadmcp-upgrade-test-" + Guid.NewGuid().ToString("N");
        var stopRequested = new ManualResetEventSlim();
        var exited = new ManualResetEventSlim();
        // The old broker holds the broker name like the real one: a named mutex owned by its own thread,
        // released only when the old process "exits".
        var holding = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            using var mutex = new Mutex(true, @"Local\" + pipe, out bool created);
            holding.Set();
            exited.Wait(TimeSpan.FromSeconds(30));
            if (created) mutex.ReleaseMutex();
        }) { IsBackground = true };
        owner.Start();
        Assert.True(holding.Wait(TimeSpan.FromSeconds(5)));
        var old = new PipeServer(pipe, (r, ct) =>
        {
            if (r.Operation == "broker_stop") stopRequested.Set();
            return Task.FromResult(new Response(r.RequestId, "completed", new { version = "0.0.1" }));
        });
        old.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var replaced = BrokerBootstrap.EnsureAsync(TestEnvironment.HostExecutable, timeout.Token, pipe);
            Assert.True(stopRequested.Wait(TimeSpan.FromSeconds(10)), "Older broker was not asked to stop");
            await old.StopAsync(TimeSpan.FromSeconds(1));
            Assert.False(replaced.IsCompleted, "The replacement started before the older broker exited");
            exited.Set();
            owner.Join(TimeSpan.FromSeconds(10));
            await replaced;
            var reply = await PipeClient.CallAsync(pipe, new("ping", "broker_ping"), timeout.Token);
            Assert.Equal(Wire.Version, Wire.Element(reply.Data!).Text("version"));
        }
        finally
        {
            old.Dispose();
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await PipeClient.CallAsync(pipe, new("stop", "broker_stop"), stopTimeout.Token); }
            catch (Exception e) when (Wire.IsTransportFailure(e)) { }
        }
    }
}
