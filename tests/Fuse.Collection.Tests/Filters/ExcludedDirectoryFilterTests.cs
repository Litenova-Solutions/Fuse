using Fuse.Collection.Filters;
using Fuse.Collection.Options;

namespace Fuse.Collection.Tests.Filters;

public sealed class ExcludedDirectoryFilterTests
{
    private readonly ExcludedDirectoryFilter _filter = new();

    [Fact]
    public void Include_NoExcludedDirectories_ReturnsTrue()
    {
        var options = TestHelpers.DefaultOptions();
        var candidate = TestHelpers.CreateCandidate(Path.Combine("bin", "app.dll"));

        Assert.True(_filter.Include(candidate, options));
    }

    [Fact]
    public void Include_ExcludedDirectoryInPath_ReturnsFalse()
    {
        var options = new CollectionOptions(@"C:\src", excludeDirectories: ["bin"]);
        var candidate = TestHelpers.CreateCandidate(Path.Combine("bin", "app.dll"));

        Assert.False(_filter.Include(candidate, options));
    }

    [Fact]
    public void Include_NonExcludedDirectory_ReturnsTrue()
    {
        var options = new CollectionOptions(@"C:\src", excludeDirectories: ["bin"]);
        var candidate = TestHelpers.CreateCandidate(Path.Combine("src", "Program.cs"));

        Assert.True(_filter.Include(candidate, options));
    }
}
