using Fuse.Semantics;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Warms the MSBuild toolchain once at daemon/serve start (R44). The first MSBuild-touching call per process
///     pays a fixed multi-second warmup (<see cref="Microsoft.Build.Locator.MSBuildLocator" /> registration plus
///     the first <c>MSBuildWorkspace</c> open: JIT, MSBuild assembly load, SDK resolution). In a long-lived daemon
///     that cost otherwise lands on the first <c>fuse_refactor</c> or full <c>fuse_check</c> of a session; warming
///     it in the background at startup amortizes it away.
/// </summary>
/// <remarks>
///     The warmup discovers the served root's solution/project and loads it into the R42 <see cref="WarmSolutionCache" />,
///     which registers the locator and primes the workspace in one step - so the warm load also populates the cache,
///     and the first real refactor/doctor hits a warm solution. It is fire-and-forget and best-effort (a failure is
///     swallowed so startup is never blocked), deduped with the first real load by the cache's load gate, and
///     opt-out with <c>FUSE_MSBUILD_WARMUP=0</c>. The same best-effort pattern as R38 eager index warm.
/// </remarks>
public static class MsBuildToolchainWarmer
{
    /// <summary>The environment variable that opts out of the MSBuild toolchain warmup.</summary>
    public const string EnvVar = "FUSE_MSBUILD_WARMUP";

    private static int _warmupsStarted;
    private static int _warmupsCompleted;

    /// <summary>The number of warmups started this process (a metric so a test can assert the warmup ran at startup).</summary>
    public static int WarmupsStarted => Volatile.Read(ref _warmupsStarted);

    /// <summary>The number of warmups that have completed this process.</summary>
    public static int WarmupsCompleted => Volatile.Read(ref _warmupsCompleted);

    /// <summary>Whether the warmup is enabled (default on; <c>0</c>/<c>false</c>/<c>no</c>/<c>off</c> opts out).</summary>
    /// <returns><see langword="true" /> unless explicitly opted out.</returns>
    public static bool IsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(EnvVar);
        return value is null
               || !(value.Equals("0", StringComparison.Ordinal)
                    || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("no", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("off", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Starts the background MSBuild toolchain warmup for a served root, if enabled. Returns immediately
    ///     (non-blocking); the returned task lets callers (tests) await completion. Returns <see langword="null" />
    ///     when the warmup is disabled.
    /// </summary>
    /// <param name="root">The workspace root whose solution/project to prime.</param>
    /// <param name="cache">The warm-solution cache to prime; defaults to <see cref="WarmSolutionCache.Shared" />.</param>
    /// <param name="log">A sink for a non-fatal diagnostic, or null.</param>
    /// <param name="cancellationToken">The host's lifetime token.</param>
    /// <returns>The warmup task, or <see langword="null" /> when disabled.</returns>
    public static Task? Start(string root, WarmSolutionCache? cache = null, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled())
            return null;

        var solutions = cache ?? WarmSolutionCache.Shared;
        var full = Path.GetFullPath(root);
        return Task.Run(async () =>
        {
            Interlocked.Increment(ref _warmupsStarted);
            try
            {
                var discovery = await new DotNetWorkspaceDiscoverer().DiscoverAsync(full, cancellationToken);
                var target = discovery.SolutionPath ?? discovery.ProjectPaths.FirstOrDefault();
                if (target is not null)
                    await solutions.OpenAsync(target, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort: the warmup never blocks or breaks serving. The first real refactor/doctor pays the
                // cold load if the warmup could not complete.
                log?.Invoke($"MSBuild toolchain warmup skipped: {ex.Message}");
            }
            finally
            {
                Interlocked.Increment(ref _warmupsCompleted);
            }
        }, cancellationToken);
    }
}
