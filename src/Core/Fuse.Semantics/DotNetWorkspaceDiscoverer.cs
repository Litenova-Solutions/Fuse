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
    // Path segments that mark a solution or project as a test, fixture, or sample tree rather than the repo's own
    // product surface. A solution nested under one of these must never be loaded as the repo's semantic tier ahead
    // of a root-level solution (R24): on the Fuse repo, that is why discovery once bound to tests/.../SampleShop.sln
    // instead of the root Fuse.slnx.
    private static readonly string[] FixtureSegments =
    [
        "test", "tests", "fixture", "fixtures", "sample", "samples", "example", "examples",
        "testdata", "testassets", "benchmark", "benchmarks",
    ];

    // Path segments that mark a solution as auxiliary build or tooling rather than the repo's product surface. A
    // solution under one of these (a common .NET convention: NodaTime's build/Tools.slnx, Arcade's eng/) must never
    // be loaded as the repo's semantic tier ahead of a product solution under src or the root: on NodaTime, the
    // plain alphabetical tie-break otherwise picked build/Tools.slnx (2 tooling projects) over src/NodaTime.slnx
    // (the real library), producing a thin 343-symbol graph. Deprioritized above fixtures but below product code.
    private static readonly string[] AuxiliarySegments =
    [
        "build", "eng", "tools", "scripts",
    ];

    /// <summary>The <c>fuse.json</c> key that pins the target solution or project (R24).</summary>
    public const string SolutionConfigKey = "solution";

    public Task<WorkspaceDiscoveryResult> DiscoverAsync(string root, CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(root);
        var ignored = new HashSet<string>(WorkspaceExclusions.LoadDirectoryNames(fullRoot), StringComparer.OrdinalIgnoreCase);
        var solutions = new List<string>();
        var projects = new List<string>();
        foreach (var file in EnumerateFiles(fullRoot, ignored, cancellationToken))
        {
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".sln":
                case ".slnx":
                    solutions.Add(file);
                    break;
                case ".csproj":
                    projects.Add(file);
                    break;
            }
        }

        projects.Sort(StringComparer.OrdinalIgnoreCase);
        var uniqueSolutions = DedupeCopies(solutions);

        // A fuse.json "solution" override pins the target explicitly and wins over discovery (R24).
        var pinned = ReadPinnedSolution(fullRoot);
        if (pinned is not null)
        {
            return Task.FromResult(new WorkspaceDiscoveryResult(
                WorkspaceKind.Solution, pinned, projects, fullRoot, $"solution pinned by fuse.json: {Path.GetFileName(pinned)}"));
        }

        // Rank solutions so the repo's own product solution wins: product code first (tier 0), then auxiliary
        // build/tooling solutions (tier 1), then test/fixture solutions last (tier 2); within a tier, shallower
        // (closer to the root) wins, then .sln before .slnx, then a stable path order. The tier is what keeps a
        // build/eng/tools solution from beating a src solution on the plain alphabetical tie-break (R24).
        var ranked = uniqueSolutions
            .OrderBy(s => Tier(fullRoot, s))
            .ThenBy(s => Depth(fullRoot, s))
            .ThenBy(s => Path.GetExtension(s).Equals(".sln", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        WorkspaceDiscoveryResult result;
        if (ranked.Count == 0)
        {
            result = projects.Count > 0
                ? new WorkspaceDiscoveryResult(WorkspaceKind.Projects, null, projects, fullRoot)
                : new WorkspaceDiscoveryResult(WorkspaceKind.SyntaxOnly, null, [], fullRoot);
            return Task.FromResult(result);
        }

        var best = ranked[0];
        var bestTier = Tier(fullRoot, best);

        // Never silently load a fixture or auxiliary-tooling solution as the repo's semantic tier ahead of the
        // repo's own product code: if the best solution is under a test/fixture/sample or build/eng/tools/scripts
        // tree but the repo has product projects (outside those trees), load those projects instead.
        if (bestTier > 0)
        {
            var treeKind = bestTier == FixtureTier ? "test/fixture" : "build/tooling";
            var productProjects = projects.Where(p => Tier(fullRoot, p) == ProductTier).ToList();
            if (productProjects.Count > 0)
            {
                return Task.FromResult(new WorkspaceDiscoveryResult(
                    WorkspaceKind.Projects, null, productProjects, fullRoot,
                    $"the only solutions found are under {treeKind} directories; loading the repo's product projects instead. Pin one with a fuse.json \"solution\" key."));
            }

            return Task.FromResult(new WorkspaceDiscoveryResult(
                WorkspaceKind.Solution, best, projects, fullRoot,
                $"selected a solution under a {treeKind} directory ({Relative(fullRoot, best)}); no product solution was found. Pin one with a fuse.json \"solution\" key."));
        }

        // Ambiguity among distinct-named product solutions (same tier and depth): pick the alphabetical first by
        // rule and surface the choice. A .sln and .slnx of the same base name are the same solution in two formats,
        // not an ambiguity; a deprioritized build/tooling or fixture solution is not an ambiguity either.
        var topTier = ranked
            .Where(s => Tier(fullRoot, s) == ProductTier && Depth(fullRoot, s) == Depth(fullRoot, best))
            .ToList();
        var distinctNames = topTier
            .Select(s => Path.GetFileNameWithoutExtension(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var note = distinctNames.Count > 1
            ? $"multiple root-level solutions found ({string.Join(", ", distinctNames)}); selected {Path.GetFileName(best)} by name order. Pin one with a fuse.json \"solution\" key."
            : null;

        return Task.FromResult(new WorkspaceDiscoveryResult(WorkspaceKind.Solution, best, projects, fullRoot, note));
    }

    // Reads a fuse.json "solution" key at the root and resolves it to an existing absolute path, or null when the
    // file, key, or target is absent. Kept dependency-light (a small JSON read) so the Core discoverer does not
    // depend on the CLI configuration layer.
    private static string? ReadPinnedSolution(string root)
    {
        var configPath = Path.Combine(root, "fuse.json");
        if (!File.Exists(configPath))
            return null;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.NameEquals(SolutionConfigKey) || property.Value.ValueKind != System.Text.Json.JsonValueKind.String)
                    continue;
                var value = property.Value.GetString();
                if (string.IsNullOrWhiteSpace(value))
                    return null;
                var resolved = Path.IsPathRooted(value) ? value : Path.GetFullPath(Path.Combine(root, value));
                return File.Exists(resolved) ? resolved : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }

        return null;
    }

    // Selection tiers, lowest wins: the repo's own product code, then auxiliary build/tooling solutions, then
    // test/fixture solutions. The tier is the primary discovery-ranking key (R24).
    private const int ProductTier = 0;
    private const int AuxiliaryTier = 1;
    private const int FixtureTier = 2;

    private static int Tier(string root, string path) =>
        IsFixturePath(root, path) ? FixtureTier
        : ContainsSegment(root, path, AuxiliarySegments) ? AuxiliaryTier
        : ProductTier;

    private static bool IsFixturePath(string root, string path) => ContainsSegment(root, path, FixtureSegments);

    // True when any DIRECTORY segment of the path (relative to the root, excluding the file name) is in the set.
    private static bool ContainsSegment(string root, string path, string[] segmentSet)
    {
        var relative = Relative(root, path);
        var segments = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segmentSet.Contains(segments[i], StringComparer.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static int Depth(string root, string path) =>
        Relative(root, path).Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Length;

    private static string Relative(string root, string path)
    {
        try
        {
            return Path.GetRelativePath(root, path);
        }
        catch (ArgumentException)
        {
            return path;
        }
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
/// <param name="SelectionNote">
///     A human-readable note when the selection was ambiguous, pinned by <c>fuse.json</c>, or fell back away from a
///     fixture-directory solution (R24); <see langword="null" /> for an unambiguous root-level solution. Surfaced by
///     <c>fuse_workspace action=doctor</c> so a wrong or ambiguous selection is visible.
/// </param>
public sealed record WorkspaceDiscoveryResult(
    WorkspaceKind Kind,
    string? SolutionPath,
    IReadOnlyList<string> ProjectPaths,
    string RootDirectory,
    string? SelectionNote = null);
