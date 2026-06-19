using Fuse.Collection.Filters;
using Fuse.Collection.Options;

namespace Fuse.Collection.Tests.Filters;

public sealed class EmptyFileFilterTests
{
    private readonly EmptyFileFilter _filter = new();

    [Fact]
    public void Include_ExcludeEmptyDisabled_ReturnsTrue()
    {
        var options = new CollectionOptions(@"C:\src", excludeEmptyFiles: false);
        var candidate = TestHelpers.CreateEmptyCandidate("empty.txt");

        Assert.True(_filter.Include(candidate, options));
    }

    [Fact]
    public void Include_NonEmptyFile_ReturnsTrue()
    {
        var options = TestHelpers.DefaultOptions();
        var candidate = TestHelpers.CreateCandidate("Program.cs");

        Assert.True(_filter.Include(candidate, options));
    }

    [Fact]
    public void Include_EmptyFile_ReturnsFalse()
    {
        var options = TestHelpers.DefaultOptions();
        var candidate = TestHelpers.CreateEmptyCandidate("empty.txt");

        Assert.False(_filter.Include(candidate, options));
    }
}
