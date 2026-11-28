namespace Fuse.Protocol.Analysis;

/// <summary>
///     Result of <c>fuse graph</c>: the repository's project DAG, coupling clusters, and test ownership map for
///     disjoint orchestrator slicing.
/// </summary>
/// <param name="Tree">Tree stamp when the graph was read.</param>
/// <param name="Projects">Projects in the repository and their reference edges.</param>
/// <param name="Clusters">Coupled project groups that share references or tight dependencies.</param>
/// <param name="TestOwners">Mapping from each test project to the source projects it covers.</param>
internal sealed record GraphReport(
    string Tree,
    GraphReport.ProjectNode[]? Projects = null,
    GraphReport.CouplingCluster[]? Clusters = null,
    GraphReport.TestOwnership[]? TestOwners = null)
{
    /// <summary>Tree stamp when the graph was read.</summary>
    public string Tree { get; init; } = Tree ?? "";

    /// <summary>Projects in the repository and their reference edges.</summary>
    public ProjectNode[] Projects { get; init; } = Projects ?? [];

    /// <summary>Coupled project groups.</summary>
    public CouplingCluster[] Clusters { get; init; } = Clusters ?? [];

    /// <summary>Test projects and the projects they cover.</summary>
    public TestOwnership[] TestOwners { get; init; } = TestOwners ?? [];

    /// <summary>One project in the repository DAG.</summary>
    /// <param name="Name">Project name without extension.</param>
    /// <param name="Path">Repository-relative path to the project file.</param>
    /// <param name="References">Names of projects this project references.</param>
    /// <param name="ReferencedBy">Names of projects that reference this project.</param>
    /// <param name="IsTestProject">True when the project is a test project.</param>
    /// <param name="TargetFramework">Primary target framework, or null.</param>
    /// <param name="Files">Repository-relative paths of C# files compiled by this project.</param>
    public sealed record ProjectNode(
        string Name,
        string Path,
        string[]? References = null,
        string[]? ReferencedBy = null,
        bool IsTestProject = false,
        string? TargetFramework = null,
        string[]? Files = null)
    {
        /// <summary>Names of projects this project references.</summary>
        public string[] References { get; init; } = References ?? [];

        /// <summary>Names of projects that reference this project.</summary>
        public string[] ReferencedBy { get; init; } = ReferencedBy ?? [];

        /// <summary>Repository-relative paths of C# files compiled by this project.</summary>
        public string[] Files { get; init; } = Files ?? [];
    }

    /// <summary>Projects coupled through shared dependencies or direct references.</summary>
    /// <param name="Name">Cluster name.</param>
    /// <param name="Projects">Projects belonging to this cluster.</param>
    /// <param name="SharedDependencies">Shared upstream dependencies.</param>
    public sealed record CouplingCluster(
        string Name,
        string[]? Projects = null,
        string[]? SharedDependencies = null)
    {
        /// <summary>Projects belonging to this cluster.</summary>
        public string[] Projects { get; init; } = Projects ?? [];

        /// <summary>Shared upstream dependencies.</summary>
        public string[] SharedDependencies { get; init; } = SharedDependencies ?? [];
    }

    /// <summary>One test project and the projects whose tests it runs.</summary>
    /// <param name="TestProject">Name of the test project.</param>
    /// <param name="CoveredProjects">Names of production projects covered by this test project.</param>
    public sealed record TestOwnership(string TestProject, string[]? CoveredProjects = null)
    {
        /// <summary>Names of production projects covered by this test project.</summary>
        public string[] CoveredProjects { get; init; } = CoveredProjects ?? [];
    }
}
