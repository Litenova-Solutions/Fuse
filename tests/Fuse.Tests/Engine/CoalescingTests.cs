using System.Text.Json;
using Fuse.Check;
using Fuse.Check.Model;
using Fuse.Engine;
using Fuse.Failures;
using Fuse.Protocol;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     Checks of one content key share the check in flight, with one waiter giving up leaving the work the
///     others wait for, and a finished check's answer is kept by that key in the result store and, for a
///     session, as its session file, until the store is cleared. A refusal the engine answers is logged with
///     its kind, its code and its session.
/// </summary>
public class CoalescingTests
{
    [Fact]
    public async Task One_waiter_giving_up_does_not_cancel_the_shared_check_the_others_wait_for()
    {
        using var scheduler = new Scheduler(1);
        using var givingUp = new CancellationTokenSource();
        var runs = 0;
        var cancelled = false;
        async Task<EngineResponse> Work(CancellationToken token)
        {
            Interlocked.Increment(ref runs);
            try
            {
                await Task.Delay(300, token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }

            return new EngineResponse.Acknowledged();
        }

        var staying = scheduler.RunCoalescedAsync("shared", Work, TestContext.Current.CancellationToken);
        var leaving = scheduler.RunCoalescedAsync("shared", Work, givingUp.Token);
        await givingUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);

        Assert.IsType<EngineResponse.Acknowledged>(await staying.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(1, runs);
        Assert.False(cancelled);
    }

    [Fact]
    public void A_kept_answer_is_served_by_its_key_and_written_as_a_session_file()
    {
        var state = NewStateDirectory();
        try
        {
            var store = new ResultStore(state);
            store.Keep("key-1", "worker-1", generation: 3, "0123456789ab", Report("CS0103"));

            Assert.True(store.TryGet("key-1", out var kept));
            Assert.Equal("CS0103", Assert.Single(kept.Errors).Error.Id);
            Assert.True(store.TryGetSession("worker-1", out _));

            var file = Path.Combine(state, "sessions", "worker-1.json");
            Assert.True(File.Exists(file), $"no session file at {file}");
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var stored = document.RootElement;
            Assert.Equal("worker-1", stored.GetProperty("session").GetString());
            Assert.Equal(3, stored.GetProperty("generation").GetInt64());
            Assert.Equal("0123456789ab", stored.GetProperty("tree").GetString());
            Assert.Equal(1, stored.GetProperty("errorCount").GetInt32());
            var error = Assert.Single(stored.GetProperty("errors").EnumerateArray().ToList());
            Assert.Equal("CS0103", error.GetProperty("id").GetString());
            Assert.Contains("CS0103", error.GetProperty("text").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(state);
        }
    }

    [Fact]
    public void A_session_file_is_named_by_the_session_an_orchestrator_names()
    {
        Assert.Equal("worker-1.json", ResultStore.FileNameFor("worker-1"));
        Assert.Equal("worker-1.json", ResultStore.FileNameFor("registered:worker-1"));
        Assert.Equal("claude-a-w1.json", ResultStore.FileNameFor("claude:a/w1"));
    }

    [Fact]
    public void An_answer_whose_analyzers_did_not_finish_is_kept_for_nothing()
    {
        var state = NewStateDirectory();
        try
        {
            var store = new ResultStore(state);
            store.Keep("key-1", "worker-1", generation: 1, "0123456789ab", Report("CS0103") with { AnalyzersUnfinished = 1 });

            Assert.False(store.TryGet("key-1", out _));
            Assert.False(File.Exists(Path.Combine(state, "sessions", "worker-1.json")));
        }
        finally
        {
            DeleteDirectory(state);
        }
    }

    [Fact]
    public void An_older_generation_writes_no_session_file_over_a_newer_one()
    {
        var state = NewStateDirectory();
        try
        {
            var store = new ResultStore(state);
            store.Keep("key-new", "worker-1", generation: 5, "0123456789ab", Report("CS0103"));
            store.Keep("key-old", "worker-1", generation: 4, "ba9876543210", Report("CS1002"));

            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "sessions", "worker-1.json")));
            Assert.Equal(5, document.RootElement.GetProperty("generation").GetInt64());
            Assert.Equal("CS0103", Assert.Single(document.RootElement.GetProperty("errors").EnumerateArray().ToList()).GetProperty("id").GetString());
        }
        finally
        {
            DeleteDirectory(state);
        }
    }

    [Fact]
    public void Clearing_the_store_drops_kept_answers_and_leaves_the_session_files()
    {
        var state = NewStateDirectory();
        try
        {
            var store = new ResultStore(state);
            store.Keep("key-1", "worker-1", generation: 1, "0123456789ab", Report("CS0103"));
            store.Clear();

            Assert.False(store.TryGet("key-1", out _));
            Assert.False(store.TryGetSession("worker-1", out _));
            Assert.True(File.Exists(Path.Combine(state, "sessions", "worker-1.json")));

            var kept = new KeptCheck();
            var result = new CheckResult([], 1, [], [], 0, false, 0, 0, 0);
            kept.Keep("kept-1", result);
            Assert.True(kept.TryGet("kept-1", out _));
            kept.Clear();
            Assert.False(kept.TryGet("kept-1", out _));
        }
        finally
        {
            DeleteDirectory(state);
        }
    }

    [Fact]
    public async Task A_session_check_writes_its_session_file_and_a_repeat_answers_the_same()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "a * b;", "a * undefinedValue;");
        var file = repo.Full("Lib/Calc.cs");

        var first = Assert.IsType<EngineResponse.CheckAnswered>(await engine.SendAsync(
            new EngineRequest.CheckFiles([file], WaitForLoad: true) { Session = "worker-1" })).Report;
        Assert.Contains(first.Errors, e => e.Error.Id == "CS0103");

        var sessionFile = Path.Combine(repo.Root.StateDirectory, "sessions", "worker-1.json");
        Assert.True(File.Exists(sessionFile), $"no session file at {sessionFile}; log:\n{engine.Log}");
        using (var document = JsonDocument.Parse(File.ReadAllText(sessionFile)))
        {
            Assert.Equal("worker-1", document.RootElement.GetProperty("session").GetString());
            Assert.Equal(12, document.RootElement.GetProperty("tree").GetString()!.Length);
            Assert.Contains(document.RootElement.GetProperty("errors").EnumerateArray(), e => e.GetProperty("id").GetString() == "CS0103");
        }

        var repeat = Assert.IsType<EngineResponse.CheckAnswered>(await engine.SendAsync(
            new EngineRequest.CheckFiles([file], WaitForLoad: true) { Session = "worker-1" })).Report;
        Assert.Equal(first.ErrorCount, repeat.ErrorCount);
        Assert.Contains(repeat.Errors, e => e.Error.Id == "CS0103");
    }

    [Fact]
    public async Task A_check_of_every_change_answers_for_a_file_written_without_telling_the_engine()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);

        var clean = Assert.IsType<EngineResponse.CheckAnswered>(await engine.SendAsync(
            new EngineRequest.CheckChanges(WaitForLoad: true) { StampTree = true })).Report;
        Assert.Equal(0, clean.ErrorCount);

        // As a shell command writes it: no request names the file, and no hook reports it, so only a
        // confirmation of the changed set against disk can find it before the watcher does.
        repo.Replace("Lib/Calc.cs", "a * b;", "a * undefinedValue;");

        var answered = Assert.IsType<EngineResponse.CheckAnswered>(await engine.SendAsync(
            new EngineRequest.CheckChanges(WaitForLoad: true) { StampTree = true })).Report;
        Assert.Contains(answered.Errors, e => e.Error.Id == "CS0103");
        Assert.Equal(1, answered.TreeFiles);
    }

    [Fact]
    public async Task A_busy_and_an_unanswered_answer_are_logged_with_their_kind_code_and_session()
    {
        using var repo = FixtureRepo.CreateStandard();
        var log = new EngineLog(repo.Root.StateDirectory);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var scheduler = new Scheduler(1);
        scheduler.RecordDuration(SchedulerLane.Command, TimeSpan.FromSeconds(10));
        var router = new RequestRouter(repo.Root, log, scheduler, new MemoryBudget());
        SchedulerLease? lease = null;
        try
        {
            await router.InitializeAsync(shutdown.Token);
            lease = await scheduler.EnterAsync(SchedulerLane.Command, "CheckFiles", 0, TestContext.Current.CancellationToken);

            var busy = await router.HandleAsync(
                new EngineRequest.CheckFiles([repo.Full("Lib/Calc.cs")], WaitForLoad: true) { BuildId = EngineVersion.Build, RequestId = "busy-logged", WaitMs = 100, Session = "worker-9" },
                TestContext.Current.CancellationToken);
            Assert.IsType<EngineResponse.Busy>(busy);
            lease.Dispose();
            lease = null;

            var unanswered = await router.HandleAsync(
                new EngineRequest.CheckFiles([" "], WaitForLoad: true) { BuildId = EngineVersion.Build, RequestId = "invalid-logged", Session = "worker-9" },
                TestContext.Current.CancellationToken);
            Assert.Equal(ErrorCode.InvalidPath, Assert.IsType<EngineResponse.Unanswered>(unanswered).Code);

            var lines = ReadLog(log);
            Assert.Contains(lines, l => l.Contains("CheckFiles", StringComparison.Ordinal) && l.Contains("Busy", StringComparison.Ordinal) && l.Contains("worker-9", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.Contains("CheckFiles", StringComparison.Ordinal) && l.Contains("InvalidPath", StringComparison.Ordinal) && l.Contains("worker-9", StringComparison.Ordinal));
        }
        finally
        {
            lease?.Dispose();
            await shutdown.CancelAsync();
            await router.WaitForPreloadAsync();
            router.Dispose();
        }
    }

    private static CheckReport Report(string id) => new(
        [new ReportedError(new CompilerError("Lib/Calc.cs", 10, 5, id, "the name does not exist"))],
        1,
        ["Lib"],
        [],
        0,
        false,
        1,
        1,
        0);

    private static string NewStateDirectory()
    {
        var state = Path.Combine(Path.GetTempPath(), "fuse-result-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        return state;
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A state directory a test could not delete is in the temp directory, which the system cleans.
        }
    }

    /// <summary>What the router wrote to its log, read with write sharing while background work may still append.</summary>
    private static List<string> ReadLog(EngineLog log)
    {
        if (!File.Exists(log.FilePath))
            return [];
        using var stream = new FileStream(log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return [.. reader.ReadToEnd().Split('\n')];
    }
}
