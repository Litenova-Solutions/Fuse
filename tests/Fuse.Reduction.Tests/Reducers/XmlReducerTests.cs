using Fuse.Reduction.Options;
using Fuse.Reduction.Reducers.Implementations;

namespace Fuse.Reduction.Tests.Reducers;

public sealed class XmlReducerTests
{
    private readonly XmlReducer _reducer = new();
    private readonly ReductionOptions _options = new();

    [Fact]
    public void Extension_ReturnsXml()
    {
        Assert.Equal(".xml", _reducer.Extension);
    }

    [Fact]
    public void Reduce_RemovesCommentsAndCollapsesWhitespace()
    {
        const string input = """
            <!-- root -->
            <root>
                <item>value</item>
            </root>
            """;

        var result = _reducer.Reduce(input, _options);

        Assert.DoesNotContain("<!--", result);
        Assert.Contains("<root><item>value</item></root>", result);
    }
}
