using System.Diagnostics;
using Fuse.Check.Model;
using Fuse.Failures;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Check;
using Fuse.Protocol.Testing;
using Fuse.Testing.Model;
using Fuse.Testing.Model.Caching;
namespace Fuse.Engine.Routing;

/// <summary>
///     Turns what a feature returns, and a <see cref="FuseException"/> that ended a request, into the engine's answer
///     on the wire. It is the one place that knows which case of a response carries each result.
/// </summary>
internal static class ResponseMapper
{
    /// <summary>The answer to a check: the errors the changes introduced and what the check covered.</summary>
    public static EngineResponse Answered(CheckResult result) => new EngineResponse.CheckAnswered(Report(result));

    /// <summary>The answer to a test plan request: the runs to execute and the summary to print.</summary>
    public static EngineResponse Answered(TestPlanResult result) => new EngineResponse.PlanAnswered(Plan(result));

    /// <summary>The answer to a request that <paramref name="failure"/> ended: its code, and its message, which names the fix.</summary>
    public static EngineResponse Unanswered(FuseException failure) => new EngineResponse.Unanswered(failure.Code, failure.Message);

    /// <summary>A check result as the client receives it, each error carrying the cause to print under it and the repair packet a brief check attached.</summary>
    public static CheckReport Report(CheckResult result) => new(
        [.. result.Errors.Select(e => new ReportedError(e.Error, Reported(e.Cause), e.IsCauseLeftOut) { GeneratedFile = e.GeneratedFile, CausedBy = e.CausedBy, Repair = Repaired(e.Repair) })],
        result.FilesChecked,
        [.. result.Projects],
        [.. result.DeclarationsChangedIn],
        result.DependentProjectsChecked,
        result.CheckedWholeProjects,
        result.ErrorCount,
        result.ErrorFileCount,
        result.LeftToOtherSessions)
    {
        AnalyzersUnfinished = result.AnalyzersUnfinished,
        CompilationWideLeftOut = [.. result.CompilationWideLeftOut],
        FilesNotAttributed = result.FilesNotAttributed,
        NonCSharpChanged = result.NonCSharpChanged,
        NonCSharpSample = [.. result.NonCSharpSample],
        ScopedPaths = [.. result.ScopedPaths],
        WholeProjects = [.. result.WholeProjectCauses.Select(c => new ReportedWholeProject(c.Project, [.. c.ChangedFiles], [.. c.Causes]))],
    };

    /// <summary>A test plan as the client receives it, each run with the mode the client starts it in and its project file's absolute path.</summary>
    /// <param name="noCache">True when the plan skipped the slice cache, so no run answers from it.</param>
    public static TestPlan Plan(TestPlanResult result, bool noCache = false) => new(
        [.. result.Runs.Select(r => new TestRun(r.Project.Absolute, r.Name, Mode(r.Mode), r.Filter, r.UsesTestingPlatform, r.RunSettings, r.FilterArguments, r.TargetPaths)
        {
            FromCache = !noCache && r.CachedResult is not null,
            CacheReason = CacheReasonOf(r.CachedResult, noCache),
            SliceKey = noCache ? null : r.SliceKey,
        })],
        result.SelectedTests,
        result.TotalTests,
        result.Summary)
    {
        ContainerGatedTests = result.ContainerGatedTests,
        ContainerGatedProjects = [.. result.ContainerGatedProjects],
    };

    /// <summary>A repair packet as the client receives it, the source window as one text, or null when the check attached none.</summary>
    private static ReportedRepairPacket? Repaired(RepairPacket? packet) =>
        packet is null ? null : new ReportedRepairPacket(
            SourceWindow: packet.SourceWindow.Count == 0 ? null : string.Join("\n", packet.SourceWindow),
            Candidates: [.. packet.Candidates],
            SuggestedUsings: [.. packet.SuggestedUsings],
            InterfaceStub: packet.InterfaceStub);

    /// <summary>
    ///     Why a run answers from the slice cache: its stored verdict, or null when it runs fresh. The client parses
    ///     this text to reuse a passing verdict without running, so its shape is a contract.
    /// </summary>
    private static string? CacheReasonOf(SliceCacheResult? cached, bool noCache) =>
        cached is null || noCache ? null : $"slice cache: {cached.Passed} passed, {cached.Failed} failed, {cached.Skipped} skipped";

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
