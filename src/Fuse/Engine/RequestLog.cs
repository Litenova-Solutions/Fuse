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

    /// <summary>Writes both lines for <paramref name="request"/>; a request that carries no id gets no phase line.</summary>
    /// <param name="request">The request, which names the files a check was scoped to.</param>
    /// <param name="phases">The phases the request went through, in order.</param>
    /// <param name="tookMs">The whole request from the moment it held the request lock, written as <see cref="Phase.Total"/>.</param>
    public void Write(EngineRequest request, PhaseTimes phases, long tookMs)
    {
        var kind = KindOf(request);
        var files = request is EngineRequest.CheckFiles check ? " " + string.Join(",", check.Files.Select(Path.GetFileName)) : "";
        _log.Write($"{kind}{files} took {tookMs} ms");
        // One line per request, naming it and timing each phase, so a measurement can be matched to its own call.
        if (request.RequestId.Length > 0)
            _log.Write(PhaseLine.Format(request.RequestId, kind, [.. phases.All, (Phase.Total, tookMs)]));
    }
}
