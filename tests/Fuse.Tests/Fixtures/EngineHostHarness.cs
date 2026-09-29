using Fuse.Engine;
using Fuse.Paths;
using Fuse.Protocol;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Fixtures;

/// <summary>
///     The engine's request loop in this process, so a test can send requests to it the way a client does without a pipe
///     and without a second process. <see cref="EngineHarness"/> drives the workspace and the checker directly; this
///     drives <see cref="EngineHost"/>, which owns the request lock and the error handling around it, so what a test sees is what an engine answers.
/// </summary>
internal sealed class EngineHostHarness : IAsyncDisposable
{
    private readonly EngineHost _host;
    private readonly EngineLog _log;
    private readonly CancellationTokenSource _shutdown;

    private EngineHostHarness(RepoRoot root, EngineLog log, EngineHost host, CancellationTokenSource shutdown)
    {
        Root = root;
        _log = log;
        _host = host;
        _shutdown = shutdown;
    }

    public RepoRoot Root { get; }

    /// <summary>Starts a host over <paramref name="repo"/>, initialized as the engine initializes it.</summary>
    /// <param name="repo">The repository to serve; a standard fixture when null.</param>
    public static async Task<EngineHostHarness> StartAsync(FixtureRepo? repo = null)
    {
        var fixture = repo ?? FixtureRepo.CreateStandard();
        var log = new EngineLog(fixture.Root.StateDirectory);
        var host = new EngineHost(fixture.Root, log);
        // The engine's shutdown token, which a real engine cancels before it disposes the host: the background preload
        // watches it, and would otherwise keep loading into a disposed workspace.
        var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await host.InitializeAsync(shutdown.Token);
        return new EngineHostHarness(fixture.Root, log, host, shutdown);
    }

    /// <summary>Sends one request, with the version stamp a client sends.</summary>
    public Task<EngineResponse> SendAsync(EngineRequest request, CancellationToken? cancellationToken = null) =>
        _host.HandleAsync(request with { Version = EngineVersion.Build }, cancellationToken ?? TestContext.Current.CancellationToken);

    /// <summary>Sends a check for the given repository-relative files, or for every change when none are given.</summary>
    public Task<EngineResponse> CheckAsync(params string[] files) =>
        SendAsync(new EngineRequest("", RequestKind.Check, Files: files.Length == 0 ? null : files));

    /// <summary>What the host wrote to its log, for a test that needs to see the request lifecycle.</summary>
    public string Log => File.Exists(_log.FilePath) ? File.ReadAllText(_log.FilePath) : "";

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        await _host.WaitForPreloadAsync();
        _host.Dispose();
        _shutdown.Dispose();
    }
}
