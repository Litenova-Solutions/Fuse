using System.Security.Cryptography;
using Fuse.Collection;

namespace Fuse.Semantics;

/// <summary>
///     Discovers the .NET workspace under a root directory: a single solution, a set of projects, or a
///     syntax-only set of loose <c>.cs</c> files.
/// </summary>
/// <remarks>
///     Discovery order is solution, then projects, then syntax-only fallback. A single solution is preferred
///     when exactly one exists (a <c>.sln</c> ahead of a <c>.slnx</c>); otherwise all projects are indexed, or
///     loose source files when no project exists. Build and tooling directories are pruned during the walk from
///     the shared <see cref="WorkspaceExclusions" /> set (plus the repository's <c>.fuseignore</c>), and any
///     nested version-control root is pruned too, so Claude Code worktrees and other embedded checkouts under
///     the root are never enumerated.
/// </remarks>
public sealed class DotNetWorkspaceDiscoverer
{

    /// <summary>
    ///     Discovers the workspace under a root directory.
    /// </summary>
    /// <param name="root">The workspace root.</param>
    /// <param name="cancellationToken">A token to cancel the discovery.</param>
    /// <returns>The discovery result describing how the workspace should be loaded.</returns>
    public Task<WorkspaceDiscoveryResult> DiscoverAsync(string root, CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(root);
        var ignored = new HashSet<string>(WorkspaceExclusions.LoadDirectoryNames(fullRoot), StringComparer.OrdinalIgnoreCase);
        var solutions = new List<string>();
        var solutionsX = new List<string>();
        var projects = new List<string>();
        foreach (var file in EnumerateFiles(fullRoot, ignored, cancellationToken))
        {
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".sln":
                    solutions.Add(file);
                    break;
                case ".slnx":
                    solutionsX.Add(file);
                    break;
                case ".csproj":
                    projects.Add(file);
                    break;
            }
        }

        projects.Sort(StringComparer.OrdinalIgnoreCase);

        // Collapse byte-identical solution copies (a duplicated checkout or backup) so an extra copy cannot flip
        // the single-solution decision into projects mode. Combined with nested-VCS pruning above, this makes the
        // mode robust to duplication rather than amplifying it.
        var uniqueSolutions = DedupeCopies(solutions);
        var uniqueSolutionsX = DedupeCopies(solutionsX);

        // Prefer a single solution: a unique .sln first, otherwise a unique .slnx when no .sln is present.
        var solution = uniqueSolutions.Count == 1 ? uniqueSolutions[0]
            : uniqueSolutions.Count == 0 && uniqueSolutionsX.Count == 1 ? uniqueSolutionsX[0]
            : null;

        WorkspaceDiscoveryResult result;
        if (solution is not null)
            result = new WorkspaceDiscoveryResult(WorkspaceKind.Solution, solution, projects, fullRoot);
        else if (projects.Count > 0)
            result = new WorkspaceDiscoveryResult(WorkspaceKind.Projects, null, projects, fullRoot);
        else
            result = new WorkspaceDiscoveryResult(WorkspaceKind.SyntaxOnly, null, [], fullRoot);

        return Task.FromResult(result);
    }

    // Keeps one path per distinct file content, preferring the shallowest then lexicographically-first path, so a
    // duplicated copy of a solution collapses to its canonical location. Empty files are never collapsed: they
    // carry no identity, and two distinct-but-empty solutions must remain distinct (ambiguous, projects mode).
    // An unreadable file is kept as-is rather than dropped.
    private static List<string> DedupeCopies(IReadOnlyList<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var path in paths
                     .OrderBy(p => p.Count(c => c is '/' or '\\'))
                     .ThenBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Add(path);
                continue;
            }

            if (bytes.Length == 0)
            {
                result.Add(path);
                continue;
            }

            if (seen.Add(Convert.ToHexString(SHA256.HashData(bytes))))
                result.Add(path);
        }

        return result;
    }

    // Recursive file walk that prunes ignored directories (so build trees are never enumerated) and any nested
    // version-control root below the top level (a worktree, submodule, or embedded clone), which is a separate
    // checkout whose files must not be indexed as part of this workspace.
    private static IEnumerable<string> EnumerateFiles(string directory, HashSet<string> ignoredDirectories, CancellationToken cancellationToken)
    {
        var root = directory;
        var stack = new Stack<string>();
        stack.Push(directory);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();

            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(current);
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var subdirectory in subdirectories)
            {
                var name = Path.GetFileName(subdirectory);
                if (ignoredDirectories.Contains(name))
                    continue;
                // Skip a nested VCS root (contains .git); the top-level root's own .git is already excluded by name.
                if (!string.Equals(subdirectory, root, StringComparison.OrdinalIgnoreCase) && WorkspaceExclusions.IsVcsRoot(subdirectory))
                    continue;
                stack.Push(subdirectory);
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(current);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
                yield return file;
        }
    }
}

/// <summary>
///     How a discovered workspace should be loaded.
/// </summary>
public enum WorkspaceKind
{
    /// <summary>A single solution file drives loading.</summary>
    Solution,

    /// <summary>One or more projects are loaded directly (no single solution).</summary>
    Projects,

    /// <summary>No project or solution; loose source files are indexed at the syntax level only.</summary>
    SyntaxOnly,
}

/// <summary>
///     The result of discovering a .NET workspace.
/// </summary>
/// <param name="Kind">How the workspace should be loaded.</param>
/// <param name="SolutionPath">The chosen solution file, when <see cref="Kind" /> is <see cref="WorkspaceKind.Solution" />.</param>
/// <param name="ProjectPaths">The discovered project files, sorted; empty for a syntax-only workspace.</param>
/// <param name="RootDirectory">The absolute workspace root.</param>
public sealed record WorkspaceDiscoveryResult(
    WorkspaceKind Kind,
    string? SolutionPath,
    IReadOnlyList<string> ProjectPaths,
    string RootDirectory);
