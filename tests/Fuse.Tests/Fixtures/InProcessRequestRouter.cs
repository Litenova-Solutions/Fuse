using Fuse.Engine.Routing;
using Fuse.Engine.Scheduling;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
using Fuse.Protocol.Transport;
using Fuse.Telemetry;
using Fuse.Testing.Planning;
namespace Fuse.Tests.Fixtures;

/// <summary>
///     The engine's request loop in this process, so a test can send requests to it the way a client does without a pipe
///     and without a second process. <see cref="InProcessEngine"/> drives the workspace and the checker directly; this
///     drives <see cref="RequestRouter"/>, which owns the request lock and the error handling around it, so what a test sees is what an engine answers.
/// </summary>
internal sealed class InProcessRequestRouter : IAsyncDisposable
{
    private readonly RequestRouter _router;
    private readonly EngineLog _log;
    private readonly CancellationTokenSource _shutdown;

    /// <summary>The fixture this router created because the test gave none, which it deletes when disposed.</summary>
    private readonly FixtureRepo? _owned;

    private InProcessRequestRouter(RepoRoot root, EngineLog log, RequestRouter router, CancellationTokenSource shutdown, FixtureRepo? owned)
    {
        Root = root;
        _log = log;
        _router = router;
        _shutdown = shutdown;
        _owned = owned;
    }

    public RepoRoot Root { get; }

    /// <summary>The router's test planner, for a test that holds a plan in its shadow preparation.</summary>
    public TestPlanner Planner => _router.Planner;

    /// <summary>Starts a router over <paramref name="repo"/>, initialized as the engine initializes it.</summary>
    /// <param name="repo">The repository to serve, which the test disposes; a standard fixture this router disposes when null.</param>
    public static Task<InProcessRequestRouter> StartAsync(FixtureRepo? repo = null) => StartAsync(repo, budget: null);

    /// <summary>A memory budget that never sheds and never answers Busy, whatever memory the machine holds.</summary>
    public static MemoryBudget NeverShedding() =>
        new(ceilingBytes: 1L << 60, workingSet: () => 0, memoryLoad: () => 0, totalAvailable: () => 1L << 60);

    /// <summary>
    ///     Starts a router with <paramref name="budget"/> as its memory budget, or the engine default when null. A test
    ///     whose answer must not depend on the runner's memory passes <see cref="NeverShedding"/>.
    /// </summary>
    public static async Task<InProcessRequestRouter> StartAsync(FixtureRepo? repo, MemoryBudget? budget)
    {
        var owned = repo is null ? FixtureRepo.CreateStandard() : null;
        var fixture = repo ?? owned!;
        var log = new EngineLog(fixture.Root.StateDirectory);
        var router = budget is null
            ? new RequestRouter(fixture.Root, log)
            : new RequestRouter(fixture.Root, log, new Scheduler(Scheduler.DefaultReaderCount), budget);
        // The engine's shutdown token, which a real engine cancels before it disposes the router: the background preload
        // watches it, and would otherwise keep loading into a disposed workspace.
        var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await router.InitializeAsync(shutdown.Token);
        return new InProcessRequestRouter(fixture.Root, log, router, shutdown, owned);
    }

    /// <summary>Sends one request, with the build id a client sends.</summary>
    public Task<EngineResponse> SendAsync(EngineRequest request, CancellationToken? cancellationToken = null) =>
        _router.HandleAsync(request with { BuildId = EngineVersion.Build }, cancellationToken ?? TestContext.Current.CancellationToken);

    /// <summary>Sends a check for the given repository-relative files, or for every change when none are given.</summary>
    public Task<EngineResponse> CheckAsync(params string[] files) =>
        SendAsync(files.Length == 0 ? new EngineRequest.CheckChanges(WaitForLoad: true) : new EngineRequest.CheckFiles(files, WaitForLoad: true));

    /// <summary>What the router wrote to its log, for a test that needs to see the request lifecycle.</summary>
    /// <remarks>
    ///     Read with write sharing: the engine appends while a test polls, and an append that meets a reader that does not
    ///     share writing fails, which the engine log drops, so the line the test waits for would never arrive.
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
        _owned?.Dispose();
    }
}
