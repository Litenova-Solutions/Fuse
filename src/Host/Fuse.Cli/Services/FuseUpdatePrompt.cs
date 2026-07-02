using Fuse.Indexing;

namespace Fuse.Cli.Services;

/// <summary>
///     The startup hook that tells a user (or auto-applies) when a newer Fuse is available, built on the
///     cache-first <see cref="FuseUpdateChecker" /> and the detached <see cref="ToolUpdateLauncher" />.
/// </summary>
/// <remarks>
///     Reads the cached check instantly and, if a newer version is known, either emits a one-line notice or (when
///     <c>FUSE_AUTO_UPDATE</c> is set and auto-update is allowed for this entry point) launches the detached
///     updater to apply after the current process exits. It then refreshes the cache in the background. The whole
///     method is best-effort and swallows every failure, so it can never delay or break the command it runs from.
/// </remarks>
public static class FuseUpdatePrompt
{
    /// <summary>The environment variable that opts into background auto-update between sessions.</summary>
    public const string AutoUpdateEnvironmentVariable = "FUSE_AUTO_UPDATE";

    /// <summary>
    ///     Emits an update notice (or launches an auto-update) from the cached check, then refreshes the cache
    ///     in the background.
    /// </summary>
    /// <param name="write">The sink for the notice line (for example stderr on the MCP server, the console on the CLI).</param>
    /// <param name="allowAutoUpdate">Whether this entry point may auto-update when <c>FUSE_AUTO_UPDATE</c> is set.</param>
    public static void Emit(Action<string> write, bool allowAutoUpdate)
    {
        try
        {
            if (!FuseUpdateChecker.IsEnabled)
                return;

            var checker = new FuseUpdateChecker();
            var status = checker.GetCachedStatus(FuseBuildInfo.Current);
            if (status is { UpdateAvailable: true, LatestVersion: { } latest })
            {
                if (allowAutoUpdate && IsTruthy(Environment.GetEnvironmentVariable(AutoUpdateEnvironmentVariable)))
                {
                    // Do not stop other hosts here: an auto-update must not kill sibling sessions. It applies once
                    // this process exits; if another Fuse process still holds the lock, it retries next session.
                    var result = new ToolUpdateLauncher().Launch(version: null, stopOtherHosts: false);
                    write(result.Launched
                        ? $"Fuse {latest} is available (current {FuseBuildInfo.Current}); auto-updating after this session (FUSE_AUTO_UPDATE). Log: {result.LogPath}"
                        : $"Fuse {latest} is available; auto-update could not launch ({result.Error}). Run 'fuse update'.");
                }
                else
                {
                    write($"Fuse {latest} is available (current {FuseBuildInfo.Current}). Run 'fuse update' to upgrade.");
                }
            }

            // Refresh the cache for the next start; never block the current command on the network.
            _ = Task.Run(() => checker.RefreshAsync(CancellationToken.None));
        }
        catch (Exception)
        {
            // The update prompt is a convenience; it must never disrupt the command it runs from.
        }
    }

    private static bool IsTruthy(string? value) =>
        value is not null
        && (string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));
}
