using Fuse.Paths;
using Fuse.Testing.Model;
using Fuse.Testing.Shadow;
using Fuse.Workspace.Generations;
namespace Fuse.Testing.Planning;

/// <summary>
///     A plan whose selections are made and whose shadow runs are not prepared yet. It holds the generation the
///     selections were made from: each test project's Roslyn projects and the graph, which are immutable, and an
///     emitter bound to that generation, so <see cref="TestPlanner.PrepareAsync"/> prepares it while other plans
///     select and prepare from generations of their own.
/// </summary>
internal abstract record PendingTestPlan
{
    private PendingTestPlan()
    {
    }

    /// <summary>The plan, when it needs no shadow preparation: nothing to run, or every test, which builds with MSBuild.</summary>
    /// <param name="Result">The answered plan.</param>
    /// <param name="Generation">The generation the selections were made from.</param>
    public sealed record Ready(TestPlanResult Result, Generation Generation) : PendingTestPlan;

    /// <summary>A plan whose shadow runs are still to be prepared, with the emitter that prepares them.</summary>
    /// <param name="Generation">The generation the selections were made from.</param>
    /// <param name="Emitter">The emitter this plan prepares its shadow runs with, bound to <paramref name="Generation"/>.</param>
    /// <param name="Work">Each test project to run, in the order the plan lists them.</param>
    /// <param name="Metadata">The counts, summary and switches the preparation carries.</param>
    public sealed record NeedsPreparation(Generation Generation, ShadowEmitter Emitter, IReadOnlyList<PlannedProjectWork> Work, PlanPrepMetadata Metadata) : PendingTestPlan;

    /// <summary>A plan that is already answered and prepares nothing.</summary>
    public static PendingTestPlan Of(TestPlanResult answered, Generation generation) => new Ready(answered, generation);

    /// <summary>A plan whose shadow runs are still to be prepared, with the emitter that prepares them.</summary>
    public static PendingTestPlan ToPrepare(
        Generation generation,
        ShadowEmitter emitter,
        IReadOnlyList<PlannedProjectWork> projects,
        int selected,
        int total,
        string summary,
        bool buildOnly = false,
        int gatedTests = 0,
        string[]? gatedProjects = null,
        bool noCache = false,
        bool runner = false,
        IReadOnlyDictionary<RepoPath, TestSelection>? selections = null) =>
        new NeedsPreparation(
            generation,
            emitter,
            projects,
            new PlanPrepMetadata(selected, total, summary, buildOnly, gatedTests, gatedProjects ?? [], noCache, runner, selections));
}
