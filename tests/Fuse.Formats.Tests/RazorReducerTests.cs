using Fuse.Languages.Abstractions.Options;
using Fuse.Formats.Reducers;

namespace Fuse.Formats.Tests.Reducers;

public sealed class RazorReducerTests
{
    private readonly RazorReducer _reducer = new();
    private readonly ReductionOptions _options = new();

    [Fact]
    public void SupportedExtensions_ContainsRazor()
    {
        Assert.Contains(".razor", _reducer.SupportedExtensions);
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
