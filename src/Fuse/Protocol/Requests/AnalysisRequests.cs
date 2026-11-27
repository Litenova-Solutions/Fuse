using Fuse.Protocol.Analysis;
namespace Fuse.Protocol.Requests;

internal abstract partial record EngineRequest
{
    /// <summary>
    ///     The projects to build for the files changed since HEAD: the owners and their dependents, reduced to the
    ///     highest projects, with the tree they were planned from. Like a test plan, it always waits for the engine to
    ///     finish loading.
    /// </summary>
    public sealed record PlanChangedBuild : EngineRequest;

    /// <summary>
    ///     Evaluates candidate patches or worktree diffs in memory (<c>fuse judge</c>), answered with
    ///     <see cref="EngineResponse.JudgeAnswered"/>.
    /// </summary>
    public sealed record Judge(JudgeRequest Spec) : EngineRequest;

    /// <summary>
    ///     Returns the repository's project DAG, coupling clusters, and test ownership (<c>fuse graph</c>), answered with
    ///     <see cref="EngineResponse.GraphAnswered"/>.
    /// </summary>
    public sealed record Graph : EngineRequest;

    /// <summary>
    ///     Returns the projects, candidate files, and test projects affected by modifying <paramref name="Paths"/>
    ///     (<c>fuse impact</c>), answered with <see cref="EngineResponse.ImpactAnswered"/>.
    /// </summary>
    public sealed record Impact(string[] Paths) : EngineRequest
    {
        /// <summary>Absolute or repository-relative paths to evaluate.</summary>
        public string[] Paths { get; init; } = Paths ?? [];
    }
}
