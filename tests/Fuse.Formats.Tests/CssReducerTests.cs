using Fuse.Languages.Abstractions.Options;
using Fuse.Formats.Reducers;

namespace Fuse.Formats.Tests.Reducers;

public sealed class CssReducerTests
{
    private readonly CssReducer _reducer = new();
    private readonly ReductionOptions _options = new();

    [Fact]
    public void SupportedExtensions_ContainsCss()
    {
        Assert.Contains(".css", _reducer.SupportedExtensions);
    }

    [Fact]
    public void Reduce_RemovesCommentsAndWhitespace()
    {
        const string input = """
            .box {
                color: red;
            }
            """;

        var result = _reducer.Reduce(input, _options);

        Assert.DoesNotContain("/*", result);
        Assert.Contains(".box{color:red;}", result);
    }
}
