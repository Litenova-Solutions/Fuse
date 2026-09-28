using System.Diagnostics;

namespace Fuse.Engine;

/// <summary>
///     The phases one request went through and how long each took, in the order they ran. The engine collects them while
///     it serves a request and writes them as one <see cref="PhaseLine"/>; the evals read that line back.
/// </summary>
internal sealed class PhaseTimes
{
    private readonly List<(string Phase, double Ms)> _phases = [];

    /// <summary>The phases recorded so far, in order.</summary>
    public IReadOnlyList<(string Phase, double Ms)> All => _phases;

    /// <summary>
    ///     Starts a stopwatch for the phase that is about to run. A null collector returns null, so a caller that is not
    ///     measuring pays nothing and needs no branch.
    /// </summary>
    public static Stopwatch? Start(PhaseTimes? phases) => phases is null ? null : Stopwatch.StartNew();

    /// <summary>Records the phase as the time since the matching <see cref="Start"/> call, or does nothing when not measuring.</summary>
    public static void Add(PhaseTimes? phases, string phase, Stopwatch? since)
    {
        if (phases is not null && since is not null)
            phases._phases.Add((phase, since.Elapsed.TotalMilliseconds));
    }

    /// <summary>Records a phase whose time was measured elsewhere, such as the whole request.</summary>
    public void Add(string phase, double ms) => _phases.Add((phase, ms));
}
