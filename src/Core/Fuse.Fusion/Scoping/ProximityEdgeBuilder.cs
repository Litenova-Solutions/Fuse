namespace Fuse.Fusion.Scoping;

/// <summary>
///     Derives low-weight structural proximity edges between files from their paths (item 7): a file is linked
///     to its test or implementation counterpart and to same-stem siblings, so expansion can reach a related
///     file the type-reference graph misses when references are incomplete.
/// </summary>
/// <remarks>
///     Edges are grouped by a base stem: the file name without extension, with a trailing test marker
///     (<c>Tests</c>, <c>Test</c>, <c>Spec</c>, <c>Specs</c>) removed, so <c>OrderService.cs</c> and
///     <c>OrderServiceTests.cs</c> share the stem <c>orderservice</c> and link. Matching is high precision: a
///     group larger than <see cref="MaxGroupSize" /> (a generic stem like <c>Program</c> shared across many
///     projects) is dropped rather than fully connected, and a very short stem is ignored.
/// </remarks>
public static class ProximityEdgeBuilder
{
    // A base stem shared by more than this many files is treated as generic and produces no edges, so a common
    // name does not connect unrelated files.
    private const int MaxGroupSize = 4;

    // Base stems shorter than this are too generic to link on.
    private const int MinStemLength = 3;

    private static readonly string[] TestSuffixes = ["tests", "test", "specs", "spec"];

    /// <summary>
    ///     Builds the proximity adjacency for the supplied normalized relative paths.
    /// </summary>
    /// <param name="normalizedPaths">The collected files' normalized relative paths.</param>
    /// <returns>
    ///     Each path mapped to the other paths sharing its base stem. Paths with no qualifying sibling are
    ///     absent from the map.
    /// </returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Build(IReadOnlyList<string> normalizedPaths)
    {
        var byStem = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var path in normalizedPaths)
        {
            var stem = BaseStem(path);
            if (stem.Length < MinStemLength)
                continue;

            if (!byStem.TryGetValue(stem, out var group))
                byStem[stem] = group = [];
            group.Add(path);
        }

        var edges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in byStem.Values)
        {
            if (group.Count < 2 || group.Count > MaxGroupSize)
                continue;

            foreach (var path in group)
            {
                var neighbours = group.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList();
                if (neighbours.Count > 0)
                    edges[path] = neighbours;
            }
        }

        return edges;
    }

    private static string BaseStem(string normalizedPath)
    {
        var slash = normalizedPath.LastIndexOf('/');
        var name = slash >= 0 ? normalizedPath[(slash + 1)..] : normalizedPath;
        var dot = name.LastIndexOf('.');
        if (dot > 0)
            name = name[..dot];

        var lowered = name.ToLowerInvariant();
        foreach (var suffix in TestSuffixes)
        {
            if (lowered.Length > suffix.Length && lowered.EndsWith(suffix, StringComparison.Ordinal))
                return lowered[..^suffix.Length];
        }

        return lowered;
    }
}
