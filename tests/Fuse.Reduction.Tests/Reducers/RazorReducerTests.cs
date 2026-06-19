using Fuse.Reduction.Options;
using Fuse.Reduction.Reducers.Implementations;

namespace Fuse.Reduction.Tests.Reducers;

public sealed class RazorReducerTests
{
    private readonly RazorReducer _reducer = new();
    private readonly ReductionOptions _options = new();

    [Fact]
    public void Extension_ReturnsRazor()
    {
        Assert.Equal(".razor", _reducer.Extension);
    }

    [Fact]
    public void Reduce_RemovesCommentsAndCollapsesMarkup()
    {
        const string input = """
            @* comment *@
            <div>
                <p>@(name)</p>
            </div>
            """;

        var result = _reducer.Reduce(input, _options);

        Assert.DoesNotContain("@* comment *@", result);
        Assert.Contains("<div><p>@(name)</p></div>", result);
    }
}
