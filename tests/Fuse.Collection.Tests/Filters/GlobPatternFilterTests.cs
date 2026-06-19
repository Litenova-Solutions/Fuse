using Fuse.Collection.Filters;
using Fuse.Collection.Options;

namespace Fuse.Collection.Tests.Filters;

public sealed class GlobPatternFilterTests
{
    private readonly GlobPatternFilter _filter = new();

    [Fact]
    public void Include_NoPatterns_ReturnsTrue()
    {
        var options = TestHelpers.DefaultOptions();
        var candidate = TestHelpers.CreateCandidate("Program.cs");

        Assert.True(_filter.Include(candidate, options));
    }

    [Fact]
    public void Include_MatchingFileNamePattern_ReturnsFalse()
    {
        var options = new CollectionOptions(@"C:\src", excludePatterns: ["*.dll"]);
        var candidate = TestHelpers.CreateCandidate("app.dll");

        Assert.False(_filter.Include(candidate, options));
    }

    [Fact]
    public void Include_MatchingRelativePathPattern_ReturnsFalse()
    {
        var options = new CollectionOptions(@"C:\src", excludePatterns: ["**/bin/**"]);
        var candidate = TestHelpers.CreateCandidate(Path.Combine("bin", "Release", "app.dll"));

        Assert.False(_filter.Include(candidate, options));
    }
}
