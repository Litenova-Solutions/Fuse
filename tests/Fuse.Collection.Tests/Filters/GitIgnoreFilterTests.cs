using DotNet.Globbing;
using Fuse.Collection.Filters;
using Fuse.Collection.Options;

namespace Fuse.Collection.Tests.Filters;

public sealed class GitIgnoreFilterTests
{
    private readonly GitIgnoreFilter _filter = new();

    [Fact]
    public void Include_RespectGitIgnoreDisabled_ReturnsTrue()
    {
        _filter.SetPatterns([Glob.Parse("**/bin/**")]);
        var options = new CollectionOptions(@"C:\src", respectGitIgnore: false);
        var candidate = TestHelpers.CreateCandidate(Path.Combine("bin", "app.dll"));

        Assert.True(_filter.Include(candidate, options));
    }

    [Fact]
    public void Include_MatchingPattern_ReturnsFalse()
    {
        _filter.SetPatterns([Glob.Parse("**/bin/**")]);
        var options = TestHelpers.DefaultOptions();
        var candidate = TestHelpers.CreateCandidate(Path.Combine("bin", "app.dll"));

        Assert.False(_filter.Include(candidate, options));
    }

    [Fact]
    public void Include_NonMatchingPattern_ReturnsTrue()
    {
        _filter.SetPatterns([Glob.Parse("**/bin/**")]);
        var options = TestHelpers.DefaultOptions();
        var candidate = TestHelpers.CreateCandidate(Path.Combine("src", "Program.cs"));

        Assert.True(_filter.Include(candidate, options));
    }
}
