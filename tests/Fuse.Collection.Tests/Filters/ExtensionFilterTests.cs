using Fuse.Collection.Filters;
using Fuse.Collection.Options;

namespace Fuse.Collection.Tests.Filters;

public sealed class ExtensionFilterTests
{
    private readonly ExtensionFilter _filter = new();

    [Fact]
    public void Include_MatchingExtension_ReturnsTrue()
    {
        var options = new CollectionOptions(@"C:\src", extensions: [".cs"]);
        var candidate = TestHelpers.CreateCandidate("Program.cs");

        Assert.True(_filter.Include(candidate, options));
    }

    [Fact]
    public void Include_NonMatchingExtension_ReturnsFalse()
    {
        var options = new CollectionOptions(@"C:\src", extensions: [".cs"]);
        var candidate = TestHelpers.CreateCandidate("data.json");

        Assert.False(_filter.Include(candidate, options));
    }

    [Fact]
    public void Include_WildcardExtension_ReturnsTrue()
    {
        var options = new CollectionOptions(@"C:\src", extensions: ["*.*"]);
        var candidate = TestHelpers.CreateCandidate("readme.txt");

        Assert.True(_filter.Include(candidate, options));
    }
}
