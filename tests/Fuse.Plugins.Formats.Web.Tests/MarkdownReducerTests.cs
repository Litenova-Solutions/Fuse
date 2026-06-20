using Fuse.Plugins.Abstractions.Options;
using Fuse.Plugins.Formats.Web.Reducers;

namespace Fuse.Plugins.Formats.Web.Tests.Reducers;

public sealed class MarkdownReducerTests
{
    private readonly MarkdownReducer _reducer = new();
    private readonly ReductionOptions _options = new();

    [Fact]
    public void SupportedExtensions_ContainsMd()
    {
        Assert.Contains(".md", _reducer.SupportedExtensions);
    }

    [Fact]
    public void Reduce_ConvertsSetextHeadingToAtx()
    {
        const string input = """
            Title
            =====
            """;

        var result = _reducer.Reduce(input, _options);

        Assert.Equal("# Title", result);
    }
}
