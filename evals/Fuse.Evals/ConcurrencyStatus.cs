namespace Fuse.Evals;

/// <summary>
///     The <c>fuse status</c> probes fired with a wave, one per caller, while the wave's requests hold the engine:
///     how long each probe waited, the queue and engine times of its request, and how its answers ended. A probe
///     that reports the engine did not answer in time counts as a timeout.
/// </summary>
/// <param name="Calls">How many probes the wave fired.</param>
/// <param name="Wall">The probes' wall times, in milliseconds.</param>
/// <param name="Queue">The probes' <c>gate</c> times, in milliseconds: waiting for the request lock.</param>
/// <param name="Engine">The probes' <c>total</c> times, in milliseconds, as the engine measured them.</param>
/// <param name="Timeouts">Probes that report the engine did not answer in time.</param>
/// <param name="BusyAnswers">Answers that are a <c>Busy</c> answer.</param>
/// <param name="Unanswered">Probes Fuse could not answer: exit code 2, or a report that it did not answer in time.</param>
internal sealed record ConcurrencyStatus(
    int Calls,
    LatencyStats Wall,
    LatencyStats Queue,
    LatencyStats Engine,
    int Timeouts,
    int BusyAnswers,
    int Unanswered);
