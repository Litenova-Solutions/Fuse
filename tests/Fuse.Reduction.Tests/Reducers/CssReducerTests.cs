using Fuse.Reduction.Options;
using Fuse.Reduction.Reducers.Implementations;

namespace Fuse.Reduction.Tests.Reducers;

public sealed class CssReducerTests
{
    private readonly CssReducer _reducer = new();
    private readonly ReductionOptions _options = new();

    [Fact]
    public void Extension_ReturnsCss()
    {
        Assert.Equal(".css", _reducer.Extension);
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
