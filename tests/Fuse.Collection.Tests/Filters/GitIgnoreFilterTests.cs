using DotNet.Globbing;
using Fuse.Collection.Filters;
using Fuse.Collection.Options;

namespace Fuse.Collection.Tests.Filters;

public sealed class GitIgnoreFilterTests
{
    private static GitIgnoreFilter FilterWith(params string[] patterns) =>
        new(patterns.Select(Glob.Parse).ToArray());

    [Fact]
    public void Include_RespectGitIgnoreDisabled_ReturnsTrue()
    {
        var filter = FilterWith("**/bin/**");
        var options = new CollectionOptions(@"C:\src", respectGitIgnore: false);
        var candidate = TestHelpers.CreateCandidate(Path.Combine("bin", "app.dll"));

        Assert.True(filter.Include(candidate, options));
    }

    [Fact]
    public void Include_MatchingPattern_ReturnsFalse()
    {
        var filter = FilterWith("**/bin/**");
        var options = TestHelpers.DefaultOptions();
        var candidate = TestHelpers.CreateCandidate(Path.Combine("bin", "app.dll"));

        Assert.False(filter.Include(candidate, options));
    }

    [Fact]
    public void Include_NonMatchingPattern_ReturnsTrue()
    {
        var filter = FilterWith("**/bin/**");
        var options = TestHelpers.DefaultOptions();
        var candidate = TestHelpers.CreateCandidate(Path.Combine("src", "Program.cs"));

        Assert.True(filter.Include(candidate, options));
    }

    [Fact]
    public void Include_EmptyPatterns_ReturnsTrue()
    {
        var filter = new GitIgnoreFilter();
        var options = TestHelpers.DefaultOptions();
        var candidate = TestHelpers.CreateCandidate(Path.Combine("bin", "app.dll"));

        Assert.True(filter.Include(candidate, options));
    }
}
