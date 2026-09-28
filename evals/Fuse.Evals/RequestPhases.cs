using Fuse.Engine;

namespace Fuse.Evals;

/// <summary>
///     Reads the engine's own account of one request back out of <c>engine.log</c>, so a measured call can be attributed
///     to the phases it went through rather than to the whole process.
/// </summary>
internal sealed record RequestPhases(string RequestId, string Kind, Dictionary<string, double> Phases)
{
    /// <summary>The engine's own total for the request, or null when the line carries none.</summary>
    public double? Total => Phases.GetValueOrDefault("total");

    /// <summary>
    ///     The line for the newest phase line whose id is not in <paramref name="seen"/>, or null when the engine wrote
    ///     none. Each id is added to <paramref name="seen"/>, so a caller walking the log in order takes one line per
    ///     request and cannot take the same one twice.
    /// </summary>
    public static RequestPhases? TakeNewest(IReadOnlyList<string> logLines, HashSet<string> seen)
    {
        for (var i = logLines.Count - 1; i >= 0; i--)
        {
            if (!PhaseLine.TryParse(logLines[i], out var id, out var kind, out var phases) || !seen.Add(id))
                continue;
            return new RequestPhases(id, kind, phases);
        }

        return null;
    }
}
