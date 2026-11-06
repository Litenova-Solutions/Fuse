namespace Fuse.Evals;

/// <summary>
///     One wave of simultaneous callers at one caller count: the wave's wall time, every caller's wall time, the
///     queue time and engine time of the wave's requests from their phase lines, what held the request lock during
///     the waits, how the answers ended, the same counts per form of caller, the status probes fired with the wave,
///     and the engine's peak working set after the wave.
/// </summary>
/// <param name="Callers">How many callers the wave ran at once, from distinct sessions.</param>
/// <param name="WallMs">Wall time from releasing every caller together until the last answer, in milliseconds.</param>
/// <param name="Wall">Every caller's wall time, in milliseconds.</param>
/// <param name="Queue">Every request's <c>gate</c> time, in milliseconds: waiting for the request lock.</param>
/// <param name="Engine">Every request's <c>total</c> time, in milliseconds, as the engine measured it.</param>
/// <param name="GateByHolder">Queue times grouped by what held the request lock longest during each wait.</param>
/// <param name="Calls">Every caller in the wave, in the order they were handed out.</param>
/// <param name="Errors">Introduced errors every answer reported; for a test plan, the tests that failed.</param>
/// <param name="Timeouts">Answers that say the engine did not answer in time; for hooks, read from the engine log.</param>
/// <param name="BusyAnswers">Answers that are a <c>Busy</c> answer; for hooks, read from the engine log.</param>
/// <param name="Unanswered">Answers Fuse could not give: exit code 2 for a command, or a timed-out hook answer from the engine log.</param>
/// <param name="StopTimeouts">Stop-hook answers that did not answer in time, read from the engine log.</param>
/// <param name="StatusTimeouts">Status probes that did not answer in time, fired with the wave.</param>
/// <param name="ByKind">The same wall, queue, engine and answer counts for each form of caller.</param>
/// <param name="Status">The status probes fired with the wave.</param>
/// <param name="EnginePeakWorkingSetMb">The engine's peak working set after the wave, in megabytes.</param>
internal sealed record ConcurrencyLevel(
    int Callers,
    double WallMs,
    LatencyStats Wall,
    LatencyStats Queue,
    LatencyStats Engine,
    Dictionary<string, LatencyStats> GateByHolder,
    List<ConcurrencyCall> Calls,
    int Errors,
    int Timeouts,
    int BusyAnswers,
    int Unanswered,
    int StopTimeouts,
    int StatusTimeouts,
    Dictionary<string, ConcurrencyKindStats> ByKind,
    ConcurrencyStatus Status,
    double EnginePeakWorkingSetMb);
