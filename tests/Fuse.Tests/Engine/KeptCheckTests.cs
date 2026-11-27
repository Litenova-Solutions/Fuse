using Fuse.Check.Model;
using Fuse.Check.Pipeline;
using Fuse.Check.Sessions;
using Fuse.Engine.Routing;
using Fuse.Engine.Scheduling;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
using Fuse.Protocol.Transport;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;
namespace Fuse.Tests.Engine;

/// <summary>
///     The answers a check keeps by what the tree held: the same tree has the same key, anything the answer depends on
///     changes it, a file that cannot be read has none, an unfinished result is kept for nothing, the answer unused the
///     longest goes past the cap, and over a fixture repository a repeated check without edits answers from what was
///     kept while an edit or another session runs the check again.
/// </summary>
public class KeptCheckTests
{
    private static readonly CheckResult Empty = new([], 0, [], [], 0, false, 0, 0, 0);

    [Fact]
    public void The_same_tree_has_the_same_key() =>
        InTempDirectory(directory =>
        {
            var file = Path.Combine(directory, "Calc.cs");
            File.WriteAllText(file, "class Calc {}\n");
            var keyed = new[] { ("Calc.cs", file) };

            var first = KeptCheck.KeyFor("all", 0, null, true, "head", 1, 1, "records", keyed);
            var second = KeptCheck.KeyFor("all", 0, null, true, "head", 1, 1, "records", keyed);

            Assert.False(string.IsNullOrEmpty(first));
            Assert.Equal(first, second);
        });

    [Fact]
    public void A_changed_file_has_another_key() =>
        InTempDirectory(directory =>
        {
            var file = Path.Combine(directory, "Calc.cs");
            File.WriteAllText(file, "class Calc {}\n");
            var keyed = new[] { ("Calc.cs", file) };

            var before = KeptCheck.KeyFor("all", 0, null, true, "head", 1, 1, "records", keyed);
            File.AppendAllText(file, "// edited\n");
            var after = KeptCheck.KeyFor("all", 0, null, true, "head", 1, 1, "records", keyed);

            Assert.NotEqual(before, after);
        });

    [Fact]
    public void A_file_that_cannot_be_read_has_no_key() =>
        InTempDirectory(directory =>
        {
            var file = Path.Combine(directory, "Missing.cs");
            File.WriteAllText(file, "class Missing {}\n");
            File.Delete(file);

            Assert.Null(KeptCheck.KeyFor("all", 0, null, true, "head", 1, 1, "records", [("Missing.cs", file)]));
        });

    [Fact]
    public void Anything_else_the_answer_depends_on_has_another_key() =>
        InTempDirectory(directory =>
        {
            var file = Path.Combine(directory, "Calc.cs");
            File.WriteAllText(file, "class Calc {}\n");
            var keyed = new[] { ("Calc.cs", file) };
            var key = KeptCheck.KeyFor("all", 0, null, true, "head", 1, 1, "records", keyed);

            Assert.NotEqual(key, KeptCheck.KeyFor("files:Calc.cs", 0, null, true, "head", 1, 1, "records", keyed));
            Assert.NotEqual(key, KeptCheck.KeyFor("all", 0, "claude:a", true, "head", 1, 1, "records", keyed));
            Assert.NotEqual(key, KeptCheck.KeyFor("all", 0, null, false, "head", 1, 1, "records", keyed));
            Assert.NotEqual(key, KeptCheck.KeyFor("all", 0, null, true, "moved", 1, 1, "records", keyed));
            Assert.NotEqual(key, KeptCheck.KeyFor("all", 0, null, true, "head", 2, 1, "records", keyed));
            Assert.NotEqual(key, KeptCheck.KeyFor("all", 0, null, true, "head", 1, 2, "records", keyed));
            Assert.NotEqual(key, KeptCheck.KeyFor("all", 0, null, true, "head", 1, 1, "changed", keyed));
            Assert.NotEqual(key, KeptCheck.KeyFor("all", 5, null, true, "head", 1, 1, "records", keyed));
        });

    [Fact]
    public void A_result_with_unfinished_analyzers_is_kept_for_nothing()
    {
        var kept = new KeptCheck();

        kept.Keep("key", Empty with { AnalyzersUnfinished = 1 });

        Assert.False(kept.TryGet("key", out _));

        kept.Keep("key", Empty);

        Assert.True(kept.TryGet("key", out var result));
        Assert.Same(Empty, result);
    }

    [Fact]
    public void The_answer_unused_the_longest_goes_past_the_cap()
    {
        var kept = new KeptCheck();
        for (var i = 0; i < 9; i++)
            kept.Keep($"kept-{i}", Empty);

        Assert.False(kept.TryGet("kept-0", out _));
        for (var i = 1; i < 9; i++)
            Assert.True(kept.TryGet($"kept-{i}", out _));
    }

    [Fact]
    public void The_records_hash_changes_when_the_record_does()
    {
        var edits = new SessionEdits(FixtureRepo.CheckoutRoot, null);

        var before = edits.RecordsHash();
        Assert.Equal(before, edits.RecordsHash());

        edits.Record("claude:a", [FixtureRepo.CheckoutRoot.PathOf("Lib/Calc.cs")]);
        var recorded = edits.RecordsHash();
        Assert.NotEqual(before, recorded);

        edits.MarkStopped("claude:a/agent-1");
        Assert.NotEqual(recorded, edits.RecordsHash());
    }

    [Fact]
    public async Task A_repeated_check_without_edits_is_answered_from_what_was_kept()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await QuietBudgetRouter.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        var file = repo.Full("Lib/Calc.cs");

        var first = Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync(file)).Report;
        var second = Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync(file)).Report;

        Assert.Equal(first.FilesChecked, second.FilesChecked);
        Assert.Equal(first.ErrorCount, second.ErrorCount);
        Assert.Equal(first.Errors.Select(e => e.Error.Message), second.Errors.Select(e => e.Error.Message));
        Assert.Equal(1, BindingCount(engine));
    }

    [Fact]
    public async Task An_edit_after_a_kept_answer_runs_the_check_again()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await QuietBudgetRouter.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        var file = repo.Full("Lib/Calc.cs");
        var kept = Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync(file)).Report;
        Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync(file));
        Assert.Equal(1, BindingCount(engine));

        repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        var third = Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync(file)).Report;

        Assert.Contains(third.Errors, e => e.Error.Message.Contains("'Add'", StringComparison.Ordinal));
        Assert.DoesNotContain(kept.Errors, e => e.Error.Message.Contains("'Add'", StringComparison.Ordinal));
        Assert.Equal(2, BindingCount(engine));
    }

    [Fact]
    public async Task A_check_from_another_session_runs_the_check_again_then_keeps_it()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await QuietBudgetRouter.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        var file = repo.Full("Lib/Calc.cs");
        Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync(file));
        var sessioned = new EngineRequest.CheckFiles([file], WaitForLoad: true) { Session = "claude:a" };

        var second = Assert.IsType<EngineResponse.CheckAnswered>(await engine.SendAsync(sessioned)).Report;
        Assert.Equal(2, BindingCount(engine));
        var third = Assert.IsType<EngineResponse.CheckAnswered>(await engine.SendAsync(sessioned)).Report;

        Assert.Equal(second.FilesChecked, third.FilesChecked);
        Assert.Equal(second.Errors.Select(e => e.Error.Message), third.Errors.Select(e => e.Error.Message));
        Assert.Equal(2, BindingCount(engine));
    }

    /// <summary>How many checks bound anything: a check answered from what was kept binds nothing more.</summary>
    private static int BindingCount(QuietBudgetRouter engine) =>
        engine.Log.Split('\n').Count(l => l.Contains("check: binding ", StringComparison.Ordinal));

    private static void InTempDirectory(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "fuse-kept-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            test(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    ///     The engine's request loop with a memory budget that reports no load, so no request sheds a kept
    ///     answer mid-test: the live budget reads the machine's memory load, which tests running alongside
    ///     push past the shed thresholds on a loaded runner.
    /// </summary>
    private sealed class QuietBudgetRouter : IAsyncDisposable
    {
        private readonly RequestRouter _router;
        private readonly EngineLog _log;
        private readonly CancellationTokenSource _shutdown;

        private QuietBudgetRouter(RequestRouter router, EngineLog log, CancellationTokenSource shutdown)
        {
            _router = router;
            _log = log;
            _shutdown = shutdown;
        }

        /// <summary>Starts a router over <paramref name="repo"/>, initialized as the engine initializes it.</summary>
        public static async Task<QuietBudgetRouter> StartAsync(FixtureRepo repo)
        {
            var log = new EngineLog(repo.Root.StateDirectory);
            var budget = new MemoryBudget(workingSet: () => 0L, memoryLoad: () => 0L, totalAvailable: () => long.MaxValue);
            var router = new RequestRouter(repo.Root, log, new Scheduler(Scheduler.DefaultReaderCount), budget);
            var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            await router.InitializeAsync(shutdown.Token);
            return new QuietBudgetRouter(router, log, shutdown);
        }

        /// <summary>Sends one request, with the build id a client sends.</summary>
        public Task<EngineResponse> SendAsync(EngineRequest request) =>
            _router.HandleAsync(request with { BuildId = EngineVersion.Build }, TestContext.Current.CancellationToken);

        /// <summary>Sends a check for the given repository-relative files, or for every change when none are given.</summary>
        public Task<EngineResponse> CheckAsync(params string[] files) =>
            SendAsync(files.Length == 0 ? new EngineRequest.CheckChanges(WaitForLoad: true) : new EngineRequest.CheckFiles(files, WaitForLoad: true));

        /// <summary>What the router wrote to its log, for a test that needs to see the request lifecycle.</summary>
        /// <remarks>
        ///     Read with write sharing: the engine appends while a test reads, and an append that meets a
        ///     reader that does not share writing fails, which the engine log drops.
        /// </remarks>
        public string Log
        {
            get
            {
                if (!File.Exists(_log.FilePath))
                    return "";
                using var stream = new FileStream(_log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync();
            await _router.WaitForPreloadAsync();
            _router.Dispose();
            _shutdown.Dispose();
        }
    }
}
