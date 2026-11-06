using Fuse.Failures;
using Fuse.Protocol;
using Fuse.Telemetry;

namespace Fuse.Engine;

/// <summary>
///     The two lines the engine logs for each request it routes to a feature: one for a person, naming the request and
///     how long it took, and one <see cref="PhaseLine"/> for the evals, which match it to their own call by request id.
/// </summary>
internal sealed class RequestLog
{
    private readonly EngineLog _log;

    public RequestLog(EngineLog log) => _log = log;

    /// <summary>
    ///     The request's case, which both lines name it by and the phase line writes as its <c>kind</c>:
    ///     <c>CheckChanges</c>, <c>CheckFiles</c>, <c>PlanAffectedTests</c> or <c>PlanAllTests</c>.
    /// </summary>
    public static string KindOf(EngineRequest request) => request.GetType().Name;

    /// <summary>The session a request carries, as a check, a scoped check or a plan names it, or the name a session registration names; null when it carries none.</summary>
    public static string? SessionOf(EngineRequest request) => request switch
    {
        EngineRequest.CheckChanges changes => changes.Session,
        EngineRequest.CheckFiles files => files.Session,
        EngineRequest.CheckPaths paths => paths.Session,
        EngineRequest.PlanAffectedTests plan => plan.Session,
        EngineRequest.BeginSession begin => begin.Name,
        EngineRequest.EndSession end => end.Name,
        _ => null,
    };

    /// <summary>
    ///     Writes the refusal line of a request the engine answered <see cref="EngineResponse.Unanswered"/> or
    ///     <see cref="EngineResponse.Busy"/>: its kind, the code name (<see cref="ErrorCode.Busy"/> for a Busy
    ///     answer, with its position and estimated wait) and the session it carried, so a caller that stays
    ///     silent on such an answer, as a hook does, still leaves the signal in the engine log by session.
    /// </summary>
    public void WriteUnanswered(EngineRequest request, EngineResponse response)
    {
        var session = SessionOf(request) ?? "-";
        switch (response)
        {
            case EngineResponse.Unanswered unanswered:
                _log.Write($"{KindOf(request)} unanswered {unanswered.Code} session={session}");
                break;
            case EngineResponse.Busy busy:
                _log.Write($"{KindOf(request)} unanswered {ErrorCode.Busy} session={session} position={busy.Position} estimatedWaitMs={busy.EstimatedWaitMs}");
                break;
        }
    }

    /// <summary>Writes both lines for <paramref name="request"/>; a request that carries no id gets no phase line.</summary>
    /// <param name="request">The request, which names the files a check was scoped to.</param>
    /// <param name="phases">The phases the request went through, in order. The gate phase is the queue wait, reported separately from the work that follows it.</param>
    /// <param name="tookMs">The request from the moment a reader took it (queue time is the gate phase, not this total; a status answer from the last completed sync writes 0), written as <see cref="Phase.Total"/>.</param>
    /// <param name="gateHolder">What held a reader longest while the request waited for one, or null when nothing did.</param>
    public void Write(EngineRequest request, PhaseTimes phases, long tookMs, string? gateHolder)
    {
        var kind = KindOf(request);
        var files = request is EngineRequest.CheckFiles { Files.Count: > 0 } check ? " " + string.Join(",", check.Files.Select(Path.GetFileName)) : "";
        _log.Write($"{kind}{files} took {tookMs} ms");
        // One line per request, naming it and timing each phase, so a measurement can be matched to its own call.
        if (request.RequestId.Length > 0)
            _log.Write(PhaseLine.Format(request.RequestId, kind, [.. phases.All, (Phase.Total, tookMs)], gateHolder));
    }
}
