using Fuse.Collection.Filters;
using Fuse.Collection.Options;

namespace Fuse.Collection.Tests.Filters;

public sealed class ExcludedFileNameFilterTests
{
    private readonly ExcludedFileNameFilter _filter = new();

    [Fact]
    public void Include_NoExcludedFiles_ReturnsTrue()
    {
        var options = TestHelpers.DefaultOptions();
        var candidate = TestHelpers.CreateCandidate("Program.cs");

        Assert.True(_filter.Include(candidate, options));
    }

    [Fact]
    public void Include_ExcludedFileName_ReturnsFalse()
    {
        var options = new CollectionOptions(@"C:\src", excludeFiles: ["appsettings.json"]);
        var candidate = TestHelpers.CreateCandidate("appsettings.json");

        Assert.False(_filter.Include(candidate, options));
    }

    [Fact]
    public void Include_DifferentFileName_ReturnsTrue()
    {
        var options = new CollectionOptions(@"C:\src", excludeFiles: ["appsettings.json"]);
        var candidate = TestHelpers.CreateCandidate("Program.cs");

        Assert.True(_filter.Include(candidate, options));
    }
}
