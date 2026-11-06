using Fuse.Graph;
using Fuse.Testing.Model;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;

namespace Fuse.Testing;

/// <summary>
///     A plan whose selections are made and whose shadow runs are not prepared yet. It holds the generation the
///     selections were made from: each test project's Roslyn projects and the graph, which are immutable, and an
///     emitter bound to that generation, so <see cref="TestPlanner.PrepareAsync"/> prepares it while other plans
///     select and prepare from generations of their own.
/// </summary>
internal sealed class PendingPlan
{
    private PendingPlan(TestPlanResult? answered, Generation generation, ShadowEmitter? emitter, IReadOnlyList<(ProjectNode Node, string? Filter, string[] FilterArguments, IReadOnlyList<Project> Variants)> projects, int selected, int total, string summary, bool buildOnly = false, int gatedTests = 0, string[]? gatedProjects = null)
    {
        Answered = answered;
        Generation = generation;
        Emitter = emitter;
        Projects = projects;
        Selected = selected;
        Total = total;
        Summary = summary;
        BuildOnly = buildOnly;
        GatedTests = gatedTests;
        GatedProjects = gatedProjects ?? [];
    }

    /// <summary>The plan, when it needs no shadow preparation: nothing to run, or every test, which builds with MSBuild.</summary>
    public TestPlanResult? Answered { get; }

    /// <summary>The generation the selections were made from, whose views the shadow runs are emitted from.</summary>
    public Generation Generation { get; }

    /// <summary>The emitter this plan prepares its shadow runs with, bound to <see cref="Generation"/>.</summary>
    public ShadowEmitter? Emitter { get; }

    /// <summary>The project graph the selections were made with.</summary>
    public RepoGraph Graph => Generation.Graph;

    /// <summary>
    ///     Each test project to run, in the order the plan lists them, with its filter and its Roslyn project per target
    ///     framework from the pinned generation. A project on Microsoft.Testing.Platform has no variants: it builds with MSBuild.
    /// </summary>
    public IReadOnlyList<(ProjectNode Node, string? Filter, string[] FilterArguments, IReadOnlyList<Project> Variants)> Projects { get; }

    /// <summary>How many tests the selections hold, counted from source.</summary>
    public int Selected { get; }

    /// <summary>How many tests the repository holds, counted from source.</summary>
    public int Total { get; }

    /// <summary>The summary of the plan when it runs anything.</summary>
    public string Summary { get; }

    /// <summary>True when every run builds with MSBuild (a configuration or framework was named), so nothing is emitted.</summary>
    public bool BuildOnly { get; }

    /// <summary>How many selected tests need a container runtime that is not running, so they run in no run.</summary>
    public int GatedTests { get; }

    /// <summary>The projects those tests are in, in ordinal order.</summary>
    public string[] GatedProjects { get; }

    /// <summary>A plan that is already answered and prepares nothing.</summary>
    public static PendingPlan Of(TestPlanResult answered, Generation generation) => new(answered, generation, null, [], 0, answered.TotalTests, answered.Summary);

    /// <summary>A plan whose shadow runs are still to be prepared, with the emitter that prepares them.</summary>
    public static PendingPlan ToPrepare(Generation generation, ShadowEmitter emitter, IReadOnlyList<(ProjectNode Node, string? Filter, string[] FilterArguments, IReadOnlyList<Project> Variants)> projects, int selected, int total, string summary, bool buildOnly = false, int gatedTests = 0, string[]? gatedProjects = null) =>
        new(null, generation, emitter, projects, selected, total, summary, buildOnly, gatedTests, gatedProjects);
}
