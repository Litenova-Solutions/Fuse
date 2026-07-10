namespace Fuse.Benchmarks;

/// <summary>
///     Shared retrieval metrics: recall, precision, F1, medians, and a deterministic bootstrap
///     confidence interval. All methods are pure and side-effect free.
/// </summary>
/// <remarks>
///     Precision is reported in a return-shape-aware way (Section 18.10). For a source-returning tool
///     like Fuse, file-level precision is relevant files returned over total files returned; the suites
///     also record returned tokens so token-level efficiency can be compared against candidate-list tools
///     separately rather than conflated.
/// </remarks>
public static class Metrics
{
    /// <summary>
    ///     Computes recall: the fraction of the ground-truth set that was retrieved.
    /// </summary>
    /// <param name="retrieved">The retrieved item set.</param>
    /// <param name="groundTruth">The ground-truth item set.</param>
    /// <returns>Recall in <c>[0, 1]</c>; <c>1.0</c> when the ground truth is empty.</returns>
    public static double Recall(ISet<string> retrieved, IReadOnlyCollection<string> groundTruth)
    {
        if (groundTruth.Count == 0)
            return 1.0;
        var hits = groundTruth.Count(retrieved.Contains);
        return (double)hits / groundTruth.Count;
    }

    /// <summary>
    ///     Computes precision: the fraction of the retrieved set that is relevant.
    /// </summary>
    /// <param name="retrieved">The retrieved item set.</param>
    /// <param name="groundTruth">The ground-truth item set.</param>
    /// <returns>Precision in <c>[0, 1]</c>; <c>0.0</c> when nothing was retrieved.</returns>
    public static double Precision(IReadOnlyCollection<string> retrieved, ISet<string> groundTruth)
    {
        if (retrieved.Count == 0)
            return 0.0;
        var hits = retrieved.Count(groundTruth.Contains);
        return (double)hits / retrieved.Count;
    }

    /// <summary>
    ///     Computes the harmonic mean of precision and recall.
    /// </summary>
    /// <param name="precision">Precision in <c>[0, 1]</c>.</param>
    /// <param name="recall">Recall in <c>[0, 1]</c>.</param>
    /// <returns>F1 in <c>[0, 1]</c>; <c>0.0</c> when both inputs are zero.</returns>
    public static double F1(double precision, double recall)
        => precision + recall == 0 ? 0.0 : 2 * precision * recall / (precision + recall);

    /// <summary>
    ///     Computes the median of a sequence.
    /// </summary>
    /// <param name="values">The values.</param>
    /// <returns>The median, or <c>0.0</c> when the sequence is empty.</returns>
    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
            return 0.0;
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>
    ///     Computes the arithmetic mean of a sequence.
    /// </summary>
    /// <param name="values">The values.</param>
    /// <returns>The mean, or <c>0.0</c> when the sequence is empty.</returns>
    public static double Mean(IReadOnlyCollection<double> values)
        => values.Count == 0 ? 0.0 : values.Sum() / values.Count;

    /// <summary>
    ///     Computes the reciprocal rank of the first relevant item in a ranked list: <c>1 / rank</c> of the
    ///     first hit (rank is 1-based), or <c>0.0</c> if no relevant item appears. The mean over queries is MRR.
    /// </summary>
    /// <param name="ranked">The retrieved items in rank order (most relevant first).</param>
    /// <param name="groundTruth">The ground-truth relevant set.</param>
    /// <returns>The reciprocal rank in <c>[0, 1]</c>.</returns>
    public static double ReciprocalRank(IReadOnlyList<string> ranked, ISet<string> groundTruth)
    {
        for (var i = 0; i < ranked.Count; i++)
            if (groundTruth.Contains(ranked[i]))
                return 1.0 / (i + 1);
        return 0.0;
    }

    /// <summary>
    ///     Computes recall at rank <paramref name="k" />: the fraction of the ground-truth set that appears in
    ///     the top <paramref name="k" /> of the ranked list.
    /// </summary>
    /// <param name="ranked">The retrieved items in rank order (most relevant first).</param>
    /// <param name="groundTruth">The ground-truth relevant set.</param>
    /// <param name="k">The rank cutoff.</param>
    /// <returns>Recall@k in <c>[0, 1]</c>; <c>1.0</c> when the ground truth is empty.</returns>
    public static double RecallAtK(IReadOnlyList<string> ranked, IReadOnlyCollection<string> groundTruth, int k)
    {
        if (groundTruth.Count == 0)
            return 1.0;
        var top = new HashSet<string>(ranked.Take(k));
        var hits = groundTruth.Count(top.Contains);
        return (double)hits / groundTruth.Count;
    }

    /// <summary>
    ///     Computes the normalized discounted cumulative gain at rank <paramref name="k" /> under binary
    ///     relevance: DCG over the top <paramref name="k" /> divided by the ideal DCG for the number of
    ///     relevant items reachable within <paramref name="k" />. Rewards placing relevant items higher.
    /// </summary>
    /// <param name="ranked">The retrieved items in rank order (most relevant first).</param>
    /// <param name="groundTruth">The ground-truth relevant set.</param>
    /// <param name="k">The rank cutoff.</param>
    /// <returns>nDCG@k in <c>[0, 1]</c>; <c>1.0</c> when the ground truth is empty, <c>0.0</c> when the ideal DCG is zero.</returns>
    public static double NdcgAtK(IReadOnlyList<string> ranked, ISet<string> groundTruth, int k)
    {
        if (groundTruth.Count == 0)
            return 1.0;
        double dcg = 0.0;
        var limit = Math.Min(k, ranked.Count);
        for (var i = 0; i < limit; i++)
            if (groundTruth.Contains(ranked[i]))
                dcg += 1.0 / Math.Log2(i + 2); // gain 1, discount log2(rank+1) with rank 1-based
        double idcg = 0.0;
        var ideal = Math.Min(k, groundTruth.Count);
        for (var i = 0; i < ideal; i++)
            idcg += 1.0 / Math.Log2(i + 2);
        return idcg == 0.0 ? 0.0 : dcg / idcg;
    }

    /// <summary>
    ///     Computes a percentile bootstrap confidence interval for the mean of a sample. Deterministic:
    ///     the resampling RNG is seeded from a fixed constant so a rerun over the same sample reproduces
    ///     the interval (Section 18.11 requires confidence intervals at small task counts).
    /// </summary>
    /// <param name="sample">The per-task metric values.</param>
    /// <param name="iterations">The number of bootstrap resamples.</param>
    /// <param name="confidence">The confidence level (for example <c>0.95</c>).</param>
    /// <param name="seed">The fixed RNG seed for reproducibility.</param>
    /// <returns>The low and high bounds of the interval; both equal the mean when the sample is empty.</returns>
    public static (double Low, double High) BootstrapCi(
        IReadOnlyList<double> sample,
        int iterations = 2000,
        double confidence = 0.95,
        int seed = 1469)
    {
        if (sample.Count == 0)
            return (0.0, 0.0);
        if (sample.Count == 1)
            return (sample[0], sample[0]);

        var random = new Random(seed);
        var means = new double[iterations];
        for (var i = 0; i < iterations; i++)
        {
            double sum = 0;
            for (var j = 0; j < sample.Count; j++)
                sum += sample[random.Next(sample.Count)];
            means[i] = sum / sample.Count;
        }

        Array.Sort(means);
        var tail = (1.0 - confidence) / 2.0;
        var low = means[(int)Math.Floor(tail * iterations)];
        var high = means[Math.Min(iterations - 1, (int)Math.Ceiling((1.0 - tail) * iterations))];
        return (low, high);
    }
}
