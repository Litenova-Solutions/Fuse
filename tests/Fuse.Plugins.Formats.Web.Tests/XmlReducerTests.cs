using Fuse.Plugins.Abstractions.Options;
using Fuse.Plugins.Formats.Web.Reducers;

namespace Fuse.Plugins.Formats.Web.Tests.Reducers;

public sealed class XmlReducerTests
{
    private readonly XmlReducer _reducer = new();
    private readonly ReductionOptions _options = new();

    [Fact]
    public void SupportedExtensions_ContainsXml()
    {
        Assert.Contains(".xml", _reducer.SupportedExtensions);
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
