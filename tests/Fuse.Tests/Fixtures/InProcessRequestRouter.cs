using Fuse.Engine;
using Fuse.Paths;
using Fuse.Protocol;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;

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

    private InProcessRequestRouter(RepoRoot root, EngineLog log, RequestRouter router, CancellationTokenSource shutdown)
    {
        Root = root;
        _log = log;
        _router = router;
        _shutdown = shutdown;
    }

    public RepoRoot Root { get; }

    /// <summary>Starts a router over <paramref name="repo"/>, initialized as the engine initializes it.</summary>
    /// <param name="repo">The repository to serve; a standard fixture when null.</param>
    public static async Task<InProcessRequestRouter> StartAsync(FixtureRepo? repo = null)
    {
        var fixture = repo ?? FixtureRepo.CreateStandard();
        var log = new EngineLog(fixture.Root.StateDirectory);
        var router = new RequestRouter(fixture.Root, log);
        // The engine's shutdown token, which a real engine cancels before it disposes the router: the background preload
        // watches it, and would otherwise keep loading into a disposed workspace.
        var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await router.InitializeAsync(shutdown.Token);
        return new InProcessRequestRouter(fixture.Root, log, router, shutdown);
    }

    /// <summary>Sends one request, with the build id a client sends.</summary>
    public Task<EngineResponse> SendAsync(EngineRequest request, CancellationToken? cancellationToken = null) =>
        _router.HandleAsync(request with { BuildId = EngineVersion.Build }, cancellationToken ?? TestContext.Current.CancellationToken);

    /// <summary>Sends a check for the given repository-relative files, or for every change when none are given.</summary>
    public Task<EngineResponse> CheckAsync(params string[] files) =>
        SendAsync(files.Length == 0 ? new EngineRequest.CheckChanges(WaitForLoad: true) : new EngineRequest.CheckFiles(files, WaitForLoad: true));

    /// <summary>What the router wrote to its log, for a test that needs to see the request lifecycle.</summary>
    public string Log => File.Exists(_log.FilePath) ? File.ReadAllText(_log.FilePath) : "";

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        await _router.WaitForPreloadAsync();
        _router.Dispose();
        _shutdown.Dispose();
    }
}
