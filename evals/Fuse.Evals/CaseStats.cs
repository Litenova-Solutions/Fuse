namespace Fuse.Evals;

/// <summary>
///     How one command's time spread over the cases of a suite, in seconds: the fastest, the median, the mean and its
///     standard deviation, P95, the slowest, and the total, so a median is never quoted without what it hides.
/// </summary>
/// <remarks>
///     The median of an even count is the mean of the two middle values, as <see cref="CorrectnessSuite.Median"/> takes
///     it. P95 uses the nearest-rank method, so P95 of 10 cases is the slowest. The standard deviation is the sample
///     standard deviation, and 0 for fewer than two cases.
/// </remarks>
internal sealed record CaseStats(int N, double Fastest, double Median, double Mean, double StandardDeviation, double P95, double Slowest, double Total)
{
    public static CaseStats Of(IEnumerable<double> seconds)
    {
        var sorted = seconds.Order().ToList();
        if (sorted.Count == 0)
            return new CaseStats(0, 0, 0, 0, 0, 0, 0, 0);

        var mean = sorted.Average();
        var deviation = sorted.Count < 2 ? 0 : Math.Sqrt(sorted.Sum(v => (v - mean) * (v - mean)) / (sorted.Count - 1));
        var p95 = sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(0.95 * sorted.Count) - 1)];
        return new CaseStats(sorted.Count, sorted[0], CorrectnessSuite.Median(sorted), mean, deviation, p95, sorted[^1], sorted.Sum());
    }
}
