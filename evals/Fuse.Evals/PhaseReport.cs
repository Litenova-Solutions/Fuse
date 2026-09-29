using System.Globalization;

namespace Fuse.Evals;

/// <summary>
///     One timed call: the wall time the client measured, and what the engine said it spent the time on. The difference
///     between the two is the client's own share: process start, the pipe, the wait for the engine, the test run.
/// </summary>
internal sealed record TimedCall(double Milliseconds, RequestPhases? Phases)
{
    /// <summary>The part of the call that was not the engine's request, in milliseconds; zero when the engine reported no total.</summary>
    public double OutsideEngineMs => Phases?.Total is { } total ? Math.Max(0, Milliseconds - total) : 0;
}

/// <summary>The per-phase times of a set of calls, as percentiles, plus the client-side share.</summary>
internal sealed record PhaseStats(Dictionary<string, LatencyStats> Phases, LatencyStats OutsideEngine, int N)
{
    /// <summary>A run with no calls behind it, so every number reads as zero rather than as absent.</summary>
    public static PhaseStats Empty { get; } = new([], new LatencyStats(0, 0, 0, 0), 0);
}

/// <summary>Collects timed calls and reduces them to the per-phase percentiles the result file carries.</summary>
internal static class PhaseReport
{
    /// <summary>Builds the report for a set of calls, in the order they were made.</summary>
    public static PhaseStats Build(IEnumerable<TimedCall> calls)
    {
        var all = calls.ToList();
        if (all.Count == 0)
            return PhaseStats.Empty;

        var names = all.SelectMany(c => c.Phases?.Phases.Keys ?? Enumerable.Empty<string>()).Distinct().Order(StringComparer.Ordinal).ToList();
        var phases = new Dictionary<string, LatencyStats>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            // A call that did not reach the phase contributes nothing rather than zero: its time went somewhere else.
            var values = all.Where(c => c.Phases?.Phases.ContainsKey(name) == true).Select(c => c.Phases!.Phases[name]).ToList();
            if (values.Count > 0)
                phases[name] = LatencyStats.Of(values);
        }

        return new PhaseStats(phases, LatencyStats.Of(all.Select(c => c.OutsideEngineMs).ToList()), all.Count);
    }


    /// <summary>The phase names in a stable order, for a human reading the result file.</summary>
    public static string Describe(PhaseStats stats) =>
        string.Join(", ", stats.Phases.Select(p => $"{p.Key} {p.Value.P50.ToString("0.0", CultureInfo.InvariantCulture)} ms"));
}
