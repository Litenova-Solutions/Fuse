using System.Text.RegularExpressions;

namespace Fuse.Evals;

/// <summary>
///     Which sessions the engine could not answer in a concurrency wave: timeouts and Busy answers read from the
///     engine log lines the wave wrote. Hooks stay silent on both, so their output carries neither; the engine log
///     names the request kind, the code and the session, which is how a signal is matched to its caller.
/// </summary>
/// <param name="TimedOutSessions">Sessions whose request the engine answered with Timeout.</param>
/// <param name="BusySessions">Sessions whose request the engine answered with Busy.</param>
internal sealed record ConcurrencySignals(
    HashSet<string> TimedOutSessions,
    HashSet<string> BusySessions)
{
    /// <summary>
    ///     Reads the signals for <paramref name="sessions"/> from the engine log <paramref name="lines"/> written since
    ///     the wave started. A line counts when it names a request kind and a code; a line that names no session in
    ///     <paramref name="sessions"/> counts for no caller. A build whose engine logs no codes reads as no signals.
    /// </summary>
    public static ConcurrencySignals Parse(IReadOnlyList<string> lines, IReadOnlyCollection<string> sessions)
    {
        var timedOut = new HashSet<string>(StringComparer.Ordinal);
        var busy = new HashSet<string>(StringComparer.Ordinal);
        if (sessions.Count == 0)
            return new ConcurrencySignals(timedOut, busy);
        // Sessions differ by trailing digits, so a session matches only with no digit on either side.
        var pattern = new Regex($"(?<![0-9])({string.Join("|", sessions.OrderByDescending(s => s.Length).Select(Regex.Escape))})(?![0-9])", RegexOptions.CultureInvariant);
        foreach (var line in lines)
        {
            if (!NamesKind(line))
                continue;
            var isBusy = line.Contains("Busy", StringComparison.Ordinal);
            var isTimeout = line.Contains("Timeout", StringComparison.Ordinal);
            if (!isBusy && !isTimeout)
                continue;
            foreach (Match match in pattern.Matches(line))
            {
                if (isBusy)
                    busy.Add(match.Groups[1].Value);
                if (isTimeout)
                    timedOut.Add(match.Groups[1].Value);
            }
        }

        return new ConcurrencySignals(timedOut, busy);
    }

    /// <summary>Whether <paramref name="line"/> names a request kind a wave's callers send.</summary>
    private static bool NamesKind(string line) =>
        line.Contains("CheckFiles", StringComparison.Ordinal)
        || line.Contains("CheckPaths", StringComparison.Ordinal)
        || line.Contains("CheckChanges", StringComparison.Ordinal)
        || line.Contains("PlanAffectedTests", StringComparison.Ordinal)
        || line.Contains("PlanAllTests", StringComparison.Ordinal);
}
