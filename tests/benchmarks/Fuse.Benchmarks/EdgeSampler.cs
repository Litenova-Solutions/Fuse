namespace Fuse.Benchmarks;

/// <summary>
///     Deterministically samples predicted graph edges for adjudication: up to a fixed number per edge type,
///     chosen with a seeded shuffle so a rerun produces the same sample. Used by the semantics suite's
///     corpus-adjudication mode, where a human or strong model labels the sample and the suite reports
///     precision over the OSS corpus.
/// </summary>
public static class EdgeSampler
{
    /// <summary>
    ///     Samples up to <paramref name="perType" /> edges of each edge type.
    /// </summary>
    /// <param name="edges">The predicted edges, each a (from, to, type) triple.</param>
    /// <param name="perType">The maximum number of edges to sample per edge type.</param>
    /// <param name="seed">A fixed RNG seed so the sample is reproducible.</param>
    /// <returns>The sampled edges, ordered by type then by the original triple for stability.</returns>
    public static IReadOnlyList<SampledEdge> Sample(
        IReadOnlyList<SampledEdge> edges, int perType, int seed)
    {
        var sampled = new List<SampledEdge>();
        foreach (var group in edges
                     .GroupBy(e => e.Type, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var ordered = group
                .OrderBy(e => e.From, StringComparer.Ordinal)
                .ThenBy(e => e.To, StringComparer.Ordinal)
                .ToList();
            if (ordered.Count <= perType)
            {
                sampled.AddRange(ordered);
                continue;
            }

            // Seeded Fisher-Yates over indices, then take the first perType, so the choice is reproducible and
            // independent of the input order beyond the stable pre-sort above.
            var random = new Random(HashSeed(seed, group.Key));
            var indices = Enumerable.Range(0, ordered.Count).ToArray();
            for (var i = indices.Length - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (indices[i], indices[j]) = (indices[j], indices[i]);
            }

            foreach (var index in indices.Take(perType).OrderBy(x => x))
                sampled.Add(ordered[index]);
        }

        return sampled;
    }

    // Combines the base seed with the edge type so different types shuffle independently but reproducibly.
    private static int HashSeed(int seed, string type)
    {
        unchecked
        {
            var hash = seed;
            foreach (var ch in type)
                hash = (hash * 31) + ch;
            return hash;
        }
    }
}

/// <summary>
///     A predicted edge selected for adjudication.
/// </summary>
/// <param name="From">The source node id.</param>
/// <param name="To">The target node id.</param>
/// <param name="Type">The edge type.</param>
/// <param name="Repo">The repository the edge was predicted in, or null for a fixture.</param>
public sealed record SampledEdge(string From, string To, string Type, string? Repo = null);
