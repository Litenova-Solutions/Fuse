namespace Fuse.Inspection;

/// <summary>One test project and the projects whose tests it runs.</summary>
/// <param name="TestProject">Name of the test project.</param>
/// <param name="CoveredProjects">Names of production projects covered by this test project.</param>
internal sealed record TestCoverage(string TestProject, string[]? CoveredProjects = null)
{
    /// <summary>Names of production projects covered by this test project.</summary>
    public string[] CoveredProjects { get; init; } = CoveredProjects ?? [];
}
