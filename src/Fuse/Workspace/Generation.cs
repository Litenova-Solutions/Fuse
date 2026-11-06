using Fuse.Graph;
using Fuse.Paths;
using Microsoft.CodeAnalysis;

namespace Fuse.Workspace;

/// <summary>
///     One published state of the workspace: the loaded projects as they are on disk (<see cref="Current"/>) and
///     with every changed file at its HEAD content (<see cref="Baseline"/>), the projects as evaluated, and the
///     files that differ from HEAD, together at one instant. A check pins the newest generation when it starts
///     and reads only it, so a sync that publishes the next generation cannot change the views, the graph or the
///     changed set under it. The writer publishes a generation only when one of those values changed.
/// </summary>
/// <param name="Number">How many generations the workspace published before this one, counting from zero.</param>
/// <param name="Head">The commit the baseline is taken from, or null in a repository without commits.</param>
/// <param name="Current">The loaded projects as they are on disk.</param>
/// <param name="Baseline">The loaded projects with every changed file at its HEAD content.</param>
/// <param name="Graph">Every C# project in the repository, as the last evaluation found it.</param>
/// <param name="Changed">The C# and Razor files that differ from HEAD, including deleted ones.</param>
/// <param name="NonSources">The files that differ from HEAD and are not sources, including deleted ones.</param>
/// <param name="NonSourceChanged">How many changed files are not sources.</param>
/// <param name="NonSourceSample">The first changed non-sources by sorted relative path.</param>
/// <param name="ConfigurationGeneration">The workspace generation, which moves when projects are evaluated again.</param>
/// <param name="BaselineGeneration">The baseline generation, which moves when the baseline content does.</param>
internal sealed record Generation(
    long Number,
    string? Head,
    Solution Current,
    Solution Baseline,
    RepoGraph Graph,
    IReadOnlyList<RepoPath> Changed,
    IReadOnlyList<RepoPath> NonSources,
    int NonSourceChanged,
    IReadOnlyList<RepoPath> NonSourceSample,
    int ConfigurationGeneration,
    int BaselineGeneration);
