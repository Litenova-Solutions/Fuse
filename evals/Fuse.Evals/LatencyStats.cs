namespace Fuse.Evals;

/// <summary>The median, P95 and slowest of a set of timings, in milliseconds, and how many there were.</summary>
internal sealed record LatencyStats(double P50, double P95, double Max, int N)
{
    /// <summary>
    ///     The statistics of <paramref name="values"/>, each rounded to one decimal. Percentiles use the nearest-rank
    ///     method, so P95 of 10 or 15 samples is the slowest one. No values give all zeros.
    /// </summary>
    public static LatencyStats Of(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        double At(double q) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(q * sorted.Count) - 1)];
        return new LatencyStats(Math.Round(At(0.5), 1), Math.Round(At(0.95), 1), Math.Round(sorted.LastOrDefault(), 1), sorted.Count);
    }
}
