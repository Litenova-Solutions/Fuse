namespace Fuse.Inspection;

/// <summary>Projects coupled through shared dependencies or direct references.</summary>
/// <param name="Name">Cluster name.</param>
/// <param name="Projects">Projects belonging to this cluster.</param>
/// <param name="SharedDependencies">Shared upstream dependencies.</param>
internal sealed record ProjectCluster(
    string Name,
    string[]? Projects = null,
    string[]? SharedDependencies = null)
{
    /// <summary>Projects belonging to this cluster.</summary>
    public string[] Projects { get; init; } = Projects ?? [];

    /// <summary>Shared upstream dependencies.</summary>
    public string[] SharedDependencies { get; init; } = SharedDependencies ?? [];
}
