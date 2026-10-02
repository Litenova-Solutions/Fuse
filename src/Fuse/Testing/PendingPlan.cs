using Fuse.Graph;
using Fuse.Testing.Model;
using Microsoft.CodeAnalysis;

namespace Fuse.Testing;

/// <summary>
///     A plan whose selections are made under the request lock and whose shadow runs are not prepared yet. It holds the
///     snapshot the selections were made from: each test project's Roslyn projects and the graph, which are immutable, so
///     <see cref="TestPlanner.PrepareAsync"/> can run after the lock is released and still answer for that snapshot.
/// </summary>
internal sealed class PendingPlan
{
    private PendingPlan(TestPlanResult? answered, RepoGraph graph, IReadOnlyList<(ProjectNode Node, string? Filter, IReadOnlyList<Project> Variants)> projects, int selected, int total, string summary)
    {
        Answered = answered;
        Graph = graph;
        Projects = projects;
        Selected = selected;
        Total = total;
        Summary = summary;
    }

    /// <summary>The plan, when it needs no shadow preparation: nothing to run, or every test, which builds with MSBuild.</summary>
    public TestPlanResult? Answered { get; }

    /// <summary>The project graph the selections were made with.</summary>
    public RepoGraph Graph { get; }

    /// <summary>
    ///     Each test project to run, in the order the plan lists them, with its filter and its Roslyn project per target
    ///     framework from the snapshot. A project on Microsoft.Testing.Platform has no variants: it builds with MSBuild.
    /// </summary>
    public IReadOnlyList<(ProjectNode Node, string? Filter, IReadOnlyList<Project> Variants)> Projects { get; }

    /// <summary>How many tests the selections hold, counted from source.</summary>
    public int Selected { get; }

    /// <summary>How many tests the repository holds, counted from source.</summary>
    public int Total { get; }

    /// <summary>The summary of the plan when it runs anything.</summary>
    public string Summary { get; }

    /// <summary>A plan that is already answered and prepares nothing.</summary>
    public static PendingPlan Of(TestPlanResult answered, RepoGraph graph) => new(answered, graph, [], 0, answered.TotalTests, answered.Summary);

    /// <summary>A plan whose shadow runs are still to be prepared.</summary>
    public static PendingPlan ToPrepare(RepoGraph graph, IReadOnlyList<(ProjectNode Node, string? Filter, IReadOnlyList<Project> Variants)> projects, int selected, int total, string summary) =>
        new(null, graph, projects, selected, total, summary);
}
