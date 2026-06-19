using Fuse.Collection.Filters;
using Fuse.Collection.Options;

namespace Fuse.Collection.Tests.Filters;

public sealed class FileSizeFilterTests
{
    private readonly FileSizeFilter _filter = new();

    [Fact]
    public void Include_NoLimit_ReturnsTrue()
    {
        var options = TestHelpers.DefaultOptions();
        var candidate = TestHelpers.CreateCandidate("large.txt", new string('x', 5000));

        Assert.True(_filter.Include(candidate, options));
    }

    [Fact]
    public void Include_WithinLimit_ReturnsTrue()
    {
        var options = new CollectionOptions(@"C:\src", maxFileSizeKb: 10);
        var candidate = TestHelpers.CreateCandidate("small.txt", new string('x', 1024));

        Assert.True(_filter.Include(candidate, options));
    }

    [Fact]
    public void Include_ExceedsLimit_ReturnsFalse()
    {
        var options = new CollectionOptions(@"C:\src", maxFileSizeKb: 1);
        var candidate = TestHelpers.CreateCandidate("large.txt", new string('x', 2048));

        Assert.False(_filter.Include(candidate, options));
    }
}
