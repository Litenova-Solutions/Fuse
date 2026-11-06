namespace Fuse.Evals;

/// <summary>
///     What one form of caller cost in a wave: its wall times, the queue time and engine time of its requests from
///     their phase lines, and how its answers ended. A request's phase line names its case, and each case in a wave
///     comes from one form: <c>CheckFiles</c> from a post-edit hook, <c>CheckPaths</c> from a scoped check,
///     <c>CheckChanges</c> from a stop hook, and <c>PlanAffectedTests</c> from a test plan.
/// </summary>
/// <param name="Calls">How many callers of this form the wave ran.</param>
/// <param name="Wall">The callers' wall times, in milliseconds.</param>
/// <param name="Queue">The requests' <c>gate</c> times, in milliseconds: waiting for the request lock.</param>
/// <param name="Engine">The requests' <c>total</c> times, in milliseconds, as the engine measured them.</param>
/// <param name="Errors">Introduced errors the answers reported; for a test plan, the tests that failed.</param>
/// <param name="Timeouts">Answers that say the engine did not answer in time; for hooks, read from the engine log.</param>
/// <param name="BusyAnswers">Answers that are a <c>Busy</c> answer; for hooks, read from the engine log.</param>
/// <param name="Unanswered">Answers Fuse could not give: exit code 2 for a command, or a timed-out hook answer from the engine log.</param>
internal sealed record ConcurrencyKindStats(
    int Calls,
    LatencyStats Wall,
    LatencyStats Queue,
    LatencyStats Engine,
    int Errors,
    int Timeouts,
    int BusyAnswers,
    int Unanswered);
