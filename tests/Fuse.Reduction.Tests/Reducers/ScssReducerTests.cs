using Fuse.Reduction.Options;
using Fuse.Reduction.Reducers.Implementations;

namespace Fuse.Reduction.Tests.Reducers;

public sealed class ScssReducerTests
{
    private readonly ScssReducer _reducer = new();
    private readonly ReductionOptions _options = new();

    [Fact]
    public void Extension_ReturnsScss()
    {
        Assert.Equal(".scss", _reducer.Extension);
    }

    [Fact]
    public void Reduce_RemovesCommentsAndWhitespace()
    {
        const string input = """
            // theme
            $color: red;
            .box {
                color: $color;
            }
            """;

        var result = _reducer.Reduce(input, _options);

        Assert.DoesNotContain("// theme", result);
        Assert.Contains("$color:red;.box{color:$color;}", result);
    }
}
