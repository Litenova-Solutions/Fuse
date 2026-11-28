using Fuse.Paths;
using Fuse.Testing.Model;
namespace Fuse.Testing.Planning;

/// <summary>The counts, summary and switches a pending plan carries into preparation, shared by its project work.</summary>
/// <param name="Selected">How many tests the selections hold, counted from source.</param>
/// <param name="Total">How many tests the repository holds, counted from source.</param>
/// <param name="Summary">The summary of the plan when it runs anything.</param>
/// <param name="BuildOnly">True when every run builds with MSBuild (a configuration or framework was named), so nothing is emitted.</param>
/// <param name="GatedTests">How many selected tests need a container runtime that is not running, so they run in no run.</param>
/// <param name="GatedProjects">The projects those tests are in, in ordinal order.</param>
/// <param name="NoCache">True when slice cache lookups are bypassed for this plan (<c>--clean</c> / <c>--no-cache</c>).</param>
/// <param name="Runner">Whether <c>global.json</c> opts <c>dotnet test</c> into Microsoft.Testing.Platform.</param>
/// <param name="Selections">Selection per test project file, for recomputing shadow-direct platform arguments when needed.</param>
internal sealed record PlanPrepMetadata(
    int Selected,
    int Total,
    string Summary,
    bool BuildOnly,
    int GatedTests,
    string[] GatedProjects,
    bool NoCache,
    bool Runner,
    IReadOnlyDictionary<RepoPath, TestSelection>? Selections);
