namespace Fuse.Inspection;

/// <summary>The repository's project graph: its projects, the clusters they form, and which test projects cover them.</summary>
/// <param name="Tree">Tree stamp when the graph was read.</param>
/// <param name="Projects">Projects in the repository and their reference edges.</param>
/// <param name="Clusters">Coupled project groups.</param>
/// <param name="TestOwners">Test projects and the projects they cover.</param>
internal sealed record ProjectGraph(
    string Tree,
    GraphProject[]? Projects = null,
    ProjectCluster[]? Clusters = null,
    TestCoverage[]? TestOwners = null)
{
    /// <summary>Tree stamp when the graph was read.</summary>
    public string Tree { get; init; } = Tree ?? "";

    /// <summary>Projects in the repository and their reference edges.</summary>
    public GraphProject[] Projects { get; init; } = Projects ?? [];

    /// <summary>Coupled project groups.</summary>
    public ProjectCluster[] Clusters { get; init; } = Clusters ?? [];

    /// <summary>Test projects and the projects they cover.</summary>
    public TestCoverage[] TestOwners { get; init; } = TestOwners ?? [];
}
