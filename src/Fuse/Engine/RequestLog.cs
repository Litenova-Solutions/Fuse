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

    /// <summary>Writes both lines for <paramref name="request"/>; a request that carries no id gets no phase line.</summary>
    /// <param name="request">The request, which names the files it was scoped to.</param>
    /// <param name="phases">The phases the request went through, in order.</param>
    /// <param name="tookMs">The whole request from the moment it held the request lock, written as <see cref="Phase.Total"/>.</param>
    public void Write(EngineRequest request, PhaseTimes phases, long tookMs)
    {
        _log.Write($"{request.Kind} {(request.Files is null ? "all" : string.Join(",", request.Files.Select(Path.GetFileName)))} took {tookMs} ms");
        // One line per request, naming it and timing each phase, so a measurement can be matched to its own call.
        if (request.RequestId.Length > 0)
            _log.Write(PhaseLine.Format(request.RequestId, request.Kind.ToString(), [.. phases.All, (Phase.Total, tookMs)]));
    }
}
