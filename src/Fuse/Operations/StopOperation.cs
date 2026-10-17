using Fuse.Engine.Client;
using Fuse.Paths;

namespace Fuse.Operations;

/// <summary>
///     <c>fuse stop</c>: stops the repository's engine, or with <c>--all</c> the engine of every repository Fuse keeps state
///     for, and says what it did. The next request starts a new engine, so this is also how an engine restarts, as after
///     changing an analyzer the repository builds itself.
/// </summary>
internal static class StopOperation
{
    /// <param name="root">The repository whose engine stops, or null with <paramref name="all"/>.</param>
    /// <param name="all">True to stop the engine of every repository with a state directory.</param>
    /// <param name="cancellationToken">Cancels the waiting.</param>
    public static async Task<OperationResult> RunAsync(RepoRoot? root, bool all, CancellationToken cancellationToken)
    {
        var roots = all ? KnownRoots() : root is null ? [] : [root];
        var lines = new List<string>();
        foreach (var each in roots)
        {
            var (wasRunning, killed) = await EngineStopper.StopAsync(each, cancellationToken).ConfigureAwait(false);
            if (!wasRunning)
            {
                if (!all)
                    lines.Add($"fuse: no engine runs for {each.Path}");
                continue;
            }

            lines.Add(killed is { } pid
                ? $"fuse: the engine for {each.Path} did not stop within {EngineStopper.Grace.TotalSeconds:0} s of being asked, so its process {pid} was killed"
                : $"fuse: stopped the engine for {each.Path}");
        }

        if (all && lines.Count == 0)
            lines.Add("fuse: no engine runs");
        return new OperationResult(Outcome.Clean, string.Join('\n', lines));
    }

    /// <summary>The repositories that have a state directory and still exist; an engine whose repository is gone exits on its own.</summary>
    private static List<RepoRoot> KnownRoots()
    {
        var roots = new List<RepoRoot>();
        if (!Directory.Exists(LocalState.Repositories))
            return roots;
        foreach (var state in Directory.EnumerateDirectories(LocalState.Repositories))
        {
            try
            {
                if (LocalState.RootOf(state) is { } path && Directory.Exists(path) && RepoRoot.Find(path) is { } found && !roots.Any(r => r.Path == found.Path))
                    roots.Add(found);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return roots;
    }
}
