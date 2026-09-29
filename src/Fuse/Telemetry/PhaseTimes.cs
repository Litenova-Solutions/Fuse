using System.Diagnostics;

namespace Fuse.Telemetry;

/// <summary>
///     The phases one request went through and how long each took, in the order they ran. The engine collects them while
///     it serves a request and writes them as one <see cref="PhaseLine"/>; the evals read that line back.
/// </summary>
/// <remarks>
///     A caller that is not measuring passes <see cref="None"/>, which records nothing, so the code that times a phase
///     is the same whether or not anyone reads the times.
/// </remarks>
internal sealed class PhaseTimes
{
    private readonly List<(string Phase, double Ms)>? _phases;

    /// <summary>A collector that records every phase it is given.</summary>
    public PhaseTimes()
        : this(recording: true)
    {
    }

    private PhaseTimes(bool recording) => _phases = recording ? [] : null;

    /// <summary>A collector that records nothing and starts no stopwatch, for a caller that does not read the times.</summary>
    public static PhaseTimes None { get; } = new(recording: false);

    /// <summary>The phases recorded so far, in order. Always empty on <see cref="None"/>.</summary>
    public IReadOnlyList<(string Phase, double Ms)> All => (IReadOnlyList<(string Phase, double Ms)>?)_phases ?? [];

    /// <summary>
    ///     Starts a stopwatch for the phase that is about to run, to pass to <see cref="Add(string, Stopwatch?)"/> when it
    ///     ends. <see cref="None"/> returns null, so a caller that is not measuring pays nothing and needs no branch.
    /// </summary>
    public Stopwatch? Start() => _phases is null ? null : Stopwatch.StartNew();

    /// <summary>Records <paramref name="phase"/> as the time since <paramref name="since"/> started.</summary>
    /// <param name="phase">One of the <see cref="Phase"/> names.</param>
    /// <param name="since">A stopwatch from <see cref="Start"/>, or one the caller also reads for its own log line.</param>
    public void Add(string phase, Stopwatch? since)
    {
        if (_phases is not null && since is not null)
            _phases.Add((phase, since.Elapsed.TotalMilliseconds));
    }

    /// <summary>Records a phase whose time was measured elsewhere, such as the whole request.</summary>
    public void Add(string phase, double ms) => _phases?.Add((phase, ms));
}
