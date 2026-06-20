using Fuse.Collection.Filters;
using Fuse.Collection.Options;

namespace Fuse.Collection.Tests.Filters;

public sealed class TestProjectFilterTests
{
    private readonly TestProjectFilter _filter = new();

    [Fact]
    public void Include_ExcludeTestProjectsDisabled_ReturnsTrue()
    {
        var options = new CollectionOptions(@"C:\src", excludeTestProjects: false);
        var candidate = TestHelpers.CreateCandidate(Path.Combine("MyApp.Tests", "UnitTest1.cs"));

        Assert.True(_filter.Include(candidate, options));
    }

    [Fact]
    public void Include_TestProjectDirectory_ReturnsFalse()
    {
        var options = new CollectionOptions(@"C:\src", excludeTestProjects: true);
        var candidate = TestHelpers.CreateCandidate(Path.Combine("MyApp.Tests", "UnitTest1.cs"));

        Assert.False(_filter.Include(candidate, options));
    }

    [Fact]
    public void Include_NonTestProjectDirectory_ReturnsTrue()
    {
        var options = new CollectionOptions(@"C:\src", excludeTestProjects: true);
        var candidate = TestHelpers.CreateCandidate(Path.Combine("MyApp", "Program.cs"));

        Assert.True(_filter.Include(candidate, options));
    }
}
