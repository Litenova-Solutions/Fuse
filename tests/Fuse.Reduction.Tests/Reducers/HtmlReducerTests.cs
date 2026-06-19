using Fuse.Reduction.Options;
using Fuse.Reduction.Reducers.Implementations;

namespace Fuse.Reduction.Tests.Reducers;

public sealed class HtmlReducerTests
{
    private readonly HtmlReducer _reducer = new();
    private readonly ReductionOptions _options = new();

    [Fact]
    public void Extension_ReturnsHtml()
    {
        Assert.Equal(".html", _reducer.Extension);
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
