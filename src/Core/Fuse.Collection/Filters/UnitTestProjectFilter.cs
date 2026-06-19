using Fuse.Collection.Models;
using Fuse.Collection.Options;

namespace Fuse.Collection.Filters;

/// <summary>
///     Excludes files located within unit test project directories when enabled.
/// </summary>
public sealed class UnitTestProjectFilter : IFileFilter
{
    private static readonly string[] UnitTestProjectSuffixes =
    [
        "UnitTests", "UnitTest", "Tests", "Test", "Testing",
        "TestProject", "TestSuite", "TestLib", "TestData", "TestFramework",
        "TestUtils", "TestUtilities", "TestHelper", "TestHelpers", "TestCommon",
        "TestShared", "TestSupport"
    ];

    /// <inheritdoc />
    public bool Include(FileCandidate candidate, CollectionOptions options)
    {
        if (!options.ExcludeUnitTestProjects)
            return true;

        var pathParts = candidate.RelativePath.Split(Path.DirectorySeparatorChar);
        return !pathParts.Any(part =>
            UnitTestProjectSuffixes.Any(suffix =>
                part.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));
    }
}
