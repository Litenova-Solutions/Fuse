using Fuse.Collection.Models;
using Fuse.Collection.Options;

namespace Fuse.Collection.Filters;

/// <summary>
///     Excludes files located within test project directories when enabled.
/// </summary>
public sealed class TestProjectFilter : IFileFilter
{
    private static readonly string[] TestProjectSuffixes =
    [
        "UnitTests", "Tests", "IntegrationTests", "Specs", "Test", "Testing",
        "FunctionalTests", "AcceptanceTests", "EndToEndTests", "E2ETests",
        "TestProject", "TestSuite", "TestLib", "TestData", "TestFramework",
        "TestUtils", "TestUtilities", "TestHelper", "TestHelpers", "TestCommon",
        "TestShared", "TestSupport", "Benchmark", "Benchmarks", "Performance",
        "PerformanceTests", "LoadTests", "StressTests"
    ];

    /// <inheritdoc />
    public bool Include(FileCandidate candidate, CollectionOptions options)
    {
        if (!options.ExcludeTestProjects)
            return true;

        var pathParts = candidate.RelativePath.Split(Path.DirectorySeparatorChar);
        return !pathParts.Any(part =>
            TestProjectSuffixes.Any(suffix =>
                part.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));
    }
}
