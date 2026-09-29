using Fuse.Failures;
using Fuse.Protocol;

namespace Fuse.Engine;

/// <summary>
///     Turns what a feature returns, and a <see cref="FuseException"/> that ended a request, into the engine's answer
///     on the wire. It is the one place that knows which field of a response carries each result.
/// </summary>
internal static class ResponseMapper
{
    /// <summary>The answer to a check: the errors it introduced and how far it reached.</summary>
    public static EngineResponse Answered(CheckReport report) => new(ResponseStatus.Ok, Check: report);

    /// <summary>The answer to a test plan request: the runs to execute and the summary to print.</summary>
    public static EngineResponse Answered(TestPlan plan) => new(ResponseStatus.Ok, Tests: plan);

    /// <summary>The answer to a request that <paramref name="failure"/> ended: its code, and its message, which names the fix.</summary>
    public static EngineResponse Unanswered(FuseException failure) => EngineResponse.Fail(failure.Code, failure.Message);
}
