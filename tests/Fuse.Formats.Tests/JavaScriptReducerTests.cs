using Fuse.Languages.Abstractions.Options;
using Fuse.Formats.Reducers;

namespace Fuse.Formats.Tests.Reducers;

public sealed class JavaScriptReducerTests
{
    private readonly JavaScriptReducer _reducer = new();
    private readonly ReductionOptions _options = new();

    [Fact]
    public void SupportedExtensions_ContainsJs()
    {
        Assert.Contains(".js", _reducer.SupportedExtensions);
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
