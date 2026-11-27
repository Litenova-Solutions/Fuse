using Fuse.Paths;
using Fuse.Testing.Model.Caching;
namespace Fuse.Testing.Model;

/// <summary>
///     One <c>dotnet test</c> process the client starts: one per target framework for a shadow run, one per project for
///     a build with MSBuild.
/// </summary>
/// <param name="Project">The test project file.</param>
/// <param name="Name">The project name, for output.</param>
/// <param name="Mode">A shadow run from the assembly it names, or a build with MSBuild.</param>
/// <param name="Filter">A VSTest filter expression, or null to run every test in the project.</param>
/// <param name="Runner">How the run reaches its test host: VSTest, or Microsoft.Testing.Platform.</param>
internal sealed record PlannedRun(RepoPath Project, string Name, RunMode Mode, string? Filter, TestRunnerPlan Runner)
{
    /// <summary>What the slice cache holds for this run: nothing, a key with no verdict, or a key with its verdict.</summary>
    public SliceCacheState Cache { get; init; } = new SliceCacheState.Uncached();
}
