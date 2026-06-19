using Fuse.Reduction.Options;
using Fuse.Reduction.Reducers.Implementations;

namespace Fuse.Reduction.Tests.Reducers;

public sealed class MarkdownReducerTests
{
    private readonly MarkdownReducer _reducer = new();
    private readonly ReductionOptions _options = new();

    [Fact]
    public void Extension_ReturnsMd()
    {
        Assert.Equal(".md", _reducer.Extension);
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
