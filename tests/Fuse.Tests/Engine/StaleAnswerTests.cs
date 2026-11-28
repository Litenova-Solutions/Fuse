using Fuse.Engine.Routing;
using Fuse.Engine.Scheduling;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
using Fuse.Protocol.Transport;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;
namespace Fuse.Tests.Engine;

/// <summary>
///     A named check is never answered from a kept answer for older content: after a file is broken, checked,
///     fixed back to HEAD and folded in by another request, a check naming it binds the fixed tree instead of
///     serving the kept error.
/// </summary>
public class StaleAnswerTests
{
    [Fact]
    public async Task A_named_check_after_the_fix_is_not_answered_from_the_kept_error()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await QuietBudgetRouter.StartAsync(repo);
        var file = repo.Full("Lib/Calc.cs");
        var original = File.ReadAllBytes(file);
        repo.Replace("Lib/Calc.cs", "a * b;", "a * smokeMissingLib;");

        var broken = Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync(file)).Report;
        Assert.Contains(broken.Errors, e => e.Error.Message.Contains("smokeMissingLib", StringComparison.Ordinal));

        File.WriteAllBytes(file, original);
        // Fold the fix the way a background re-check does, so the pin holds the fixed tree before the
        // named check's key is taken.
        var folded = Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync()).Report;
        Assert.Equal(0, folded.ErrorCount);
        Assert.Equal(0, folded.FilesChecked);

        var afterFix = Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync(file)).Report;

        Assert.Equal(0, afterFix.ErrorCount);
        Assert.DoesNotContain(afterFix.Errors, e => e.Error.Message.Contains("smokeMissingLib", StringComparison.Ordinal));
        Assert.Equal(2, BindingCount(engine));
    }

    /// <summary>How many checks bound anything: a check answered from what was kept binds nothing more.</summary>
    private static int BindingCount(QuietBudgetRouter engine) =>
        engine.Log.Split('\n').Count(l => l.Contains("check: binding ", StringComparison.Ordinal));

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

        /// <summary>Sends a check for the given repository-relative files, or for every change when none are given.</summary>
        public Task<EngineResponse> CheckAsync(params string[] files) =>
            SendAsync(files.Length == 0 ? new EngineRequest.CheckChanges(WaitForLoad: true) : new EngineRequest.CheckFiles(files, WaitForLoad: true));

        /// <summary>Sends one request, with the build id a client sends.</summary>
        public Task<EngineResponse> SendAsync(EngineRequest request) =>
            _router.HandleAsync(request with { BuildId = EngineVersion.Build }, TestContext.Current.CancellationToken);

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
