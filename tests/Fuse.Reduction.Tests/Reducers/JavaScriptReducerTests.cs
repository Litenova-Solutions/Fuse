using Fuse.Reduction.Options;
using Fuse.Reduction.Reducers.Implementations;

namespace Fuse.Reduction.Tests.Reducers;

public sealed class JavaScriptReducerTests
{
    private readonly JavaScriptReducer _reducer = new();
    private readonly ReductionOptions _options = new();

    [Fact]
    public void Extension_ReturnsJs()
    {
        Assert.Equal(".js", _reducer.Extension);
    }

    [Fact]
    public void Reduce_RemovesCommentsAndWhitespace()
    {
        const string input = """
            // setup
            function greet(name) {
                return "hi " + name;
            }
            """;

        var result = _reducer.Reduce(input, _options);

        Assert.DoesNotContain("// setup", result);
        Assert.Contains("function greet(name){return", result);
        Assert.Contains("name;", result);
    }
}
