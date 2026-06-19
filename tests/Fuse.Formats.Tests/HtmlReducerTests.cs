using Fuse.Languages.Abstractions.Options;
using Fuse.Formats.Reducers;

namespace Fuse.Formats.Tests.Reducers;

public sealed class HtmlReducerTests
{
    private readonly HtmlReducer _reducer = new();
    private readonly ReductionOptions _options = new();

    [Fact]
    public void SupportedExtensions_ContainsHtml()
    {
        Assert.Contains(".html", _reducer.SupportedExtensions);
    }

    [Fact]
    public void Reduce_RemovesCommentsAndCollapsesWhitespace()
    {
        const string input = """
            <!-- header -->
            <div class="box">
                <p>Hello</p>
            </div>
            """;

        var result = _reducer.Reduce(input, _options);

        Assert.DoesNotContain("<!--", result);
        Assert.Contains("<div class=box><p>Hello</p></div>", result);
    }
}
