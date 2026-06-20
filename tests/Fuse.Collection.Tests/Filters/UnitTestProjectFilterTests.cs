using Fuse.Collection.Filters;
using Fuse.Collection.Options;

namespace Fuse.Collection.Tests.Filters;

public sealed class UnitTestProjectFilterTests
{
    private readonly UnitTestProjectFilter _filter = new();

    [Fact]
    public void Include_ExcludeUnitTestProjectsDisabled_ReturnsTrue()
    {
        var options = new CollectionOptions(@"C:\src", excludeUnitTestProjects: false);
        var candidate = TestHelpers.CreateCandidate(Path.Combine("MyApp.UnitTests", "UnitTest1.cs"));

        Assert.True(_filter.Include(candidate, options));
    }

    [Fact]
    public void Include_UnitTestProjectDirectory_ReturnsFalse()
    {
        var options = new CollectionOptions(@"C:\src", excludeUnitTestProjects: true);
        var candidate = TestHelpers.CreateCandidate(Path.Combine("MyApp.UnitTests", "UnitTest1.cs"));

        Assert.False(_filter.Include(candidate, options));
    }

    [Fact]
    public void Include_NonUnitTestProjectDirectory_ReturnsTrue()
    {
        var options = new CollectionOptions(@"C:\src", excludeUnitTestProjects: true);
        var candidate = TestHelpers.CreateCandidate(Path.Combine("MyApp", "Program.cs"));

        Assert.True(_filter.Include(candidate, options));
    }
}
