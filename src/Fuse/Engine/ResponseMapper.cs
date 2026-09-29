using System.Diagnostics;
using Fuse.Check.Model;
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
    public static EngineResponse Answered(CheckResult result) => new(ResponseStatus.Ok, Check: Report(result));

    /// <summary>The answer to a test plan request: the runs to execute and the summary to print.</summary>
    public static EngineResponse Answered(TestPlan plan) => new(ResponseStatus.Ok, Tests: plan);

    /// <summary>The answer to a request that <paramref name="failure"/> ended: its code, and its message, which names the fix.</summary>
    public static EngineResponse Unanswered(FuseException failure) => EngineResponse.Fail(failure.Code, failure.Message);

    /// <summary>A check result as the client receives it, each error carrying the cause to print under it.</summary>
    public static CheckReport Report(CheckResult result) => new(
        [.. result.Errors.Select(e => new ReportedError(e.Error, Reported(e.Cause)))],
        result.FilesChecked,
        [.. result.Projects],
        [.. result.DeclarationsChangedIn],
        result.DependentProjectsChecked,
        result.CheckedWholeProjects,
        result.CausesLeftOut);

    private static ReportedCause? Reported(Cause? cause) => cause switch
    {
        null => null,
        Cause.Changed changed => new ReportedCause(CauseKind.Changed, changed.Declaration),
        Cause.Removed removed => new ReportedCause(CauseKind.Removed, removed.Declaration),
        _ => throw new UnreachableException($"a cause is changed or removed, not {cause.GetType().Name}"),
    };
}
