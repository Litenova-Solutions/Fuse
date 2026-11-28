using Fuse.Check.Model;
using Fuse.Inspection;
using Fuse.Judge.Model;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Check;
using Fuse.Protocol.Testing;
namespace Fuse.Engine.Routing;

/// <summary>
///     Maps what the inspection and judge features return to the records the pipe carries. It is the one place that
///     knows how a domain result reads on the wire, beside <see cref="ResponseMapper"/> for check and test plans.
/// </summary>
internal static class FeatureReports
{
    /// <summary>A judge verdict as the client receives it, each error carrying the cause to print under it.</summary>
    public static JudgeReport ToJudgeReport(JudgeVerdict verdict) => new(
        verdict.Mode,
        verdict.Clean,
        verdict.Summary,
        verdict.Winner,
        [.. verdict.Verdicts.Select(ToVerdict)],
        [.. verdict.Conflicts]);

    /// <summary>A project graph as the client receives it.</summary>
    public static GraphReport ToGraphReport(ProjectGraph graph) => new(
        graph.Tree,
        [.. graph.Projects.Select(project => new GraphReport.ProjectNode(
            project.Name,
            project.Path,
            [.. project.References],
            [.. project.ReferencedBy],
            project.IsTestProject,
            null,
            [.. project.Files]))],
        [.. graph.Clusters.Select(cluster => new GraphReport.CouplingCluster(
            cluster.Name,
            [.. cluster.Projects],
            [.. cluster.SharedDependencies]))],
        [.. graph.TestOwners.Select(owner => new GraphReport.TestOwnership(
            owner.TestProject,
            [.. owner.CoveredProjects]))]);

    /// <summary>An impact assessment as the client receives it.</summary>
    public static ImpactReport ToImpactReport(ImpactAssessment impact) => new(
        [.. impact.InputPaths],
        [.. impact.OwningProjects],
        [.. impact.AffectedProjects],
        [.. impact.AffectedFiles],
        [.. impact.AffectedTestProjects],
        impact.BroadReach,
        impact.Summary);

    /// <summary>A changed build as the client receives it.</summary>
    public static ChangedBuildPlan ToChangedBuildPlan(ChangedBuild build) => new(
        [.. build.BuildProjects],
        [.. build.ChangedInProjects],
        build.ChangedFileCount,
        build.TreeHash,
        build.FallbackReason,
        build.UnownedFileCount);

    /// <summary>One candidate's score as the client receives it.</summary>
    private static JudgeReport.CandidateVerdict ToVerdict(CandidateScore score) => new(
        score.Name,
        score.Rank,
        score.Compiles,
        score.ErrorCount,
        score.AnalyzerErrorCount,
        score.SurfaceBreaks,
        score.ElapsedMs,
        [.. score.Errors.Select(ToError)],
        score.ConflictReason);

    /// <summary>One scored error as the client receives it, carrying the cause to print under it.</summary>
    private static ReportedError ToError(CandidateScore.ScoredError error) =>
        new ReportedError(
            new CompilerError(error.Path, error.Line, error.Column, error.Id, error.Message, error.FromAnalyzer),
            error.Cause is null ? null : new ReportedCause(error.Cause.Removed ? CauseKind.Removed : CauseKind.Changed, error.Cause.Declaration),
            error.IsCauseLeftOut)
        {
            GeneratedFile = error.GeneratedFile,
            CausedBy = error.CausedBy,
        };
}
