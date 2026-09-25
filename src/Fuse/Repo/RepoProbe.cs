using Fuse.Dotnet;

namespace Fuse.Repo;

/// <summary>Answers, without starting an engine, whether a repository has anything for Fuse to do.</summary>
internal static class RepoProbe
{
    /// <summary>
    ///     True when git knows at least one <c>.csproj</c> in the repository (tracked, or untracked and not ignored).
    ///     Clients call this before starting an engine, so a repository without C# projects costs one git call.
    /// </summary>
    public static async Task<bool> HasCSharpProjectsAsync(RepoRoot root, CancellationToken cancellationToken)
    {
        // The index answers for tracked projects without touching the working tree; untracked ones need a scan.
        foreach (var scope in new[] { new[] { "--cached" }, ["--others", "--exclude-standard"] })
        {
            var result = await ProcessRunner.RunAsync("git", ["ls-files", .. scope, "--", "*.csproj"], root.Path, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                return true; // git could not answer; let the engine report the problem.
            if (result.Output.AsSpan().Trim().Length > 0)
                return true;
        }

        return false;
    }
}
