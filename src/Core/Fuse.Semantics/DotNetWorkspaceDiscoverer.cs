namespace Fuse.Semantics;

/// <summary>
///     Discovers the .NET workspace under a root directory: a single solution, a set of projects, or a
///     syntax-only set of loose <c>.cs</c> files.
/// </summary>
/// <remarks>
///     Discovery order is solution, then projects, then syntax-only fallback. A single solution is preferred
///     when exactly one exists (a <c>.sln</c> ahead of a <c>.slnx</c>); otherwise all projects are indexed, or
///     loose source files when no project exists. Build and tooling directories (<c>bin</c>, <c>obj</c>,
///     <c>.git</c>, <c>.fuse</c>, <c>.vs</c>, <c>node_modules</c>) are pruned during the walk.
/// </remarks>
public sealed class DotNetWorkspaceDiscoverer
{
    private static readonly HashSet<string> IgnoredDirectories =
        new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".fuse", ".vs", "node_modules" };

    /// <summary>
    ///     Discovers the workspace under a root directory.
    /// </summary>
    /// <param name="root">The workspace root.</param>
    /// <param name="cancellationToken">A token to cancel the discovery.</param>
    /// <returns>The discovery result describing how the workspace should be loaded.</returns>
    public Task<WorkspaceDiscoveryResult> DiscoverAsync(string root, CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(root);
        var solutions = new List<string>();
        var solutionsX = new List<string>();
        var projects = new List<string>();
        foreach (var file in EnumerateFiles(fullRoot, cancellationToken))
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

        // Prefer a single solution: a unique .sln first, otherwise a unique .slnx when no .sln is present.
        var solution = solutions.Count == 1 ? solutions[0]
            : solutions.Count == 0 && solutionsX.Count == 1 ? solutionsX[0]
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

    // Recursive file walk that prunes ignored directories so build trees are never enumerated.
    private static IEnumerable<string> EnumerateFiles(string directory, CancellationToken cancellationToken)
    {
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
                if (!IgnoredDirectories.Contains(name))
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
