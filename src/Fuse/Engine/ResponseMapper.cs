using System.Diagnostics;
using Fuse.Check.Model;
using Fuse.Failures;
using Fuse.Protocol;
using Fuse.Testing.Model;

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
    public static EngineResponse Answered(TestPlanResult result) => new(ResponseStatus.Ok, Tests: Plan(result));

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

    /// <summary>A test plan as the client receives it, each run with the mode the client starts it in.</summary>
    public static TestPlan Plan(TestPlanResult result) => new(
        [.. result.Runs.Select(r => new TestRun(r.Project, r.Name, Mode(r.Mode), r.Filter, r.UsesTestingPlatform))],
        result.SelectedTests,
        result.TotalTests,
        result.Summary);

    private static ReportedCause? Reported(Cause? cause) => cause switch
    {
        null => null,
        Cause.Changed changed => new ReportedCause(CauseKind.Changed, changed.Declaration),
        Cause.Removed removed => new ReportedCause(CauseKind.Removed, removed.Declaration),
        _ => throw new UnreachableException($"a cause is changed or removed, not {cause.GetType().Name}"),
    };

    private static TestRunMode Mode(RunMode mode) => mode switch
    {
        RunMode.Shadow shadow => new TestRunMode.Shadow(shadow.Assembly),
        RunMode.Build => new TestRunMode.Build(),
        _ => throw new UnreachableException($"a run mode is shadow or build, not {mode.GetType().Name}"),
    };
}
