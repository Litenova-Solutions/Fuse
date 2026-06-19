using Fuse.Plugins.Abstractions.Options;
using Fuse.Plugins.Formats.Web.Reducers;

namespace Fuse.Plugins.Formats.Web.Tests.Reducers;

public sealed class ScssReducerTests
{
    private readonly ScssReducer _reducer = new();
    private readonly ReductionOptions _options = new();

    [Fact]
    public void SupportedExtensions_ContainsScss()
    {
        Assert.Contains(".scss", _reducer.SupportedExtensions);
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
