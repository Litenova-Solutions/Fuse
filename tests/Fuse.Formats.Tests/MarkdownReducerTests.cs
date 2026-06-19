using Fuse.Languages.Abstractions.Options;
using Fuse.Formats.Reducers;

namespace Fuse.Formats.Tests.Reducers;

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
