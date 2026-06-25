using Fuse.Plugins.Abstractions.Maps;
using Microsoft.Extensions.Logging;

namespace Fuse.Fusion.Scoping;

/// <summary>
///     Builds coarse project-reference expansion edges (item 8): each candidate file is linked to the candidate
///     files in the projects its owning <c>.csproj</c> references or is referenced by, so a seed can reach a
///     related file across an assembly boundary that the intra-project type-reference graph misses.
/// </summary>
/// <remarks>
///     Edges are recovered by regex over <c>.csproj</c> text rather than by loading the MSBuild graph, matching
///     the existing project-graph generator: references injected through imported targets or globs are not
///     resolved. The cross-project fan-out is bounded per file so a seed in a small project does not pull a
///     whole large referenced project into expansion. This reads <c>.csproj</c> files from disk under the source
///     root; when none are found the result is empty and project-graph expansion is a no-op.
/// </remarks>
public static class ProjectGraphEdgeBuilder
{
    // Cap on cross-project neighbours linked to one file. A seed in a small project that references a large one
    // would otherwise pull the entire referenced project into expansion; the cap keeps the coarse edge a nudge
    // rather than a flood, and the budget-aware packer still decides what fits.
    private const int MaxNeighboursPerFile = 48;

    /// <summary>
    ///     Builds the project-reference adjacency for the supplied candidate files.
    /// </summary>
    /// <param name="sourceRoot">The absolute source root that the candidate paths are relative to.</param>
    /// <param name="candidateRelativePaths">Normalized (forward-slash) candidate paths relative to the root.</param>
    /// <param name="logger">Optional logger for non-fatal I/O failures while scanning projects.</param>
    /// <returns>
    ///     A map from a candidate path to the candidate paths reachable across a one-hop project-reference link;
    ///     empty when no <c>.csproj</c> files or no cross-project links are found.
    /// </returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Build(
        string sourceRoot,
        IReadOnlyList<string> candidateRelativePaths,
        ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(sourceRoot) || !Directory.Exists(sourceRoot) || candidateRelativePaths.Count == 0)
            return EmptyEdges;

        // Project directory (absolute, normalized) -> the set of project directories it links to (undirected:
        // a reference is recorded both ways so a library seed reaches its test project and vice versa).
        var projectDirs = new List<string>();
        var links = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        string[] csprojFiles;
        try
        {
            csprojFiles = Directory.GetFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories);
        }
        catch (IOException ex)
        {
            logger?.LogDebug(ex, "Failed to enumerate .csproj files under {SourceRoot}.", sourceRoot);
            return EmptyEdges;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger?.LogDebug(ex, "Access denied enumerating .csproj files under {SourceRoot}.", sourceRoot);
            return EmptyEdges;
        }

        foreach (var csproj in csprojFiles)
        {
            var dir = NormalizeDir(Path.GetDirectoryName(csproj)!);
            projectDirs.Add(dir);
            if (!links.ContainsKey(dir))
                links[dir] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string content;
            try
            {
                content = File.ReadAllText(csproj);
            }
            catch (IOException ex)
            {
                logger?.LogDebug(ex, "Failed to read project file {ProjectPath}.", csproj);
                continue;
            }

            foreach (var rel in CsprojProjectReferenceParser.EnumerateIncludePaths(content))
            {
                var referencedProject = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(csproj)!, rel));
                var referencedDir = NormalizeDir(Path.GetDirectoryName(referencedProject)!);
                AddLink(links, dir, referencedDir);
                AddLink(links, referencedDir, dir);
            }
        }

        if (projectDirs.Count < 2 || links.Values.All(s => s.Count == 0))
            return EmptyEdges;

        // Map each candidate to its owning project (the project directory that is the longest ancestor of the
        // candidate's absolute path), then group candidates by owning project.
        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byProject = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in candidateRelativePaths)
        {
            var abs = NormalizeDir(Path.GetFullPath(Path.Combine(sourceRoot, rel)));
            var ownerDir = LongestOwningProject(abs, projectDirs);
            if (ownerDir is null)
                continue;

            owner[rel] = ownerDir;
            if (!byProject.TryGetValue(ownerDir, out var list))
                byProject[ownerDir] = list = [];
            list.Add(rel);
        }

        var edges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in candidateRelativePaths)
        {
            if (!owner.TryGetValue(rel, out var ownerDir) || !links.TryGetValue(ownerDir, out var linkedDirs))
                continue;

            var neighbours = new List<string>();
            foreach (var linkedDir in linkedDirs)
            {
                if (byProject.TryGetValue(linkedDir, out var siblings))
                    neighbours.AddRange(siblings);
            }

            if (neighbours.Count == 0)
                continue;

            neighbours.Sort(StringComparer.OrdinalIgnoreCase);
            if (neighbours.Count > MaxNeighboursPerFile)
                neighbours = neighbours.GetRange(0, MaxNeighboursPerFile);
            edges[rel] = neighbours;
        }

        return edges.Count == 0 ? EmptyEdges : edges;
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyEdges =
        new Dictionary<string, IReadOnlyList<string>>();

    private static void AddLink(Dictionary<string, HashSet<string>> links, string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return;
        if (!links.TryGetValue(from, out var set))
            links[from] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(to);
    }

    // The owning project is the project directory that is the longest prefix of the file's directory, so a file
    // in a nested folder is attributed to its closest enclosing .csproj rather than an ancestor project.
    private static string? LongestOwningProject(string fileAbsolute, List<string> projectDirs)
    {
        string? best = null;
        foreach (var dir in projectDirs)
        {
            var prefix = dir.EndsWith('/') ? dir : dir + "/";
            if (fileAbsolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (best is null || dir.Length > best.Length))
            {
                best = dir;
            }
        }

        return best;
    }

    private static string NormalizeDir(string path) =>
        Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
}
