namespace Fuse.Protocol;

/// <summary>
///     Result of <c>fuse impact &lt;paths...&gt;</c>: the projects, files, and test projects affected by modifying the
///     named paths, for orchestrator slicing before spawning workers.
/// </summary>
/// <param name="InputPaths">Repository-relative paths that were queried.</param>
/// <param name="OwningProjects">Projects that directly own the input paths.</param>
/// <param name="AffectedProjects">Owning and transitive dependent projects.</param>
/// <param name="AffectedFiles">Candidate source files in reach of the input paths.</param>
/// <param name="AffectedTestProjects">Test projects that depend on the affected projects.</param>
/// <param name="BroadReach">True when the paths affect build props, solutions, or exceed single-slice reach.</param>
/// <param name="Summary">One-line summary of the impact radius.</param>
internal sealed record ImpactReport(
    string[]? InputPaths = null,
    string[]? OwningProjects = null,
    string[]? AffectedProjects = null,
    string[]? AffectedFiles = null,
    string[]? AffectedTestProjects = null,
    bool BroadReach = false,
    string Summary = "")
{
    /// <summary>Repository-relative paths that were queried.</summary>
    public string[] InputPaths { get; init; } = InputPaths ?? [];

    /// <summary>Projects that directly own the input paths.</summary>
    public string[] OwningProjects { get; init; } = OwningProjects ?? [];

    /// <summary>Owning and transitive dependent projects.</summary>
    public string[] AffectedProjects { get; init; } = AffectedProjects ?? [];

    /// <summary>Candidate source files in reach of the input paths.</summary>
    public string[] AffectedFiles { get; init; } = AffectedFiles ?? [];

    /// <summary>Test projects that depend on the affected projects.</summary>
    public string[] AffectedTestProjects { get; init; } = AffectedTestProjects ?? [];

    /// <summary>One-line summary of the impact radius.</summary>
    public string Summary { get; init; } = Summary ?? "";
}
