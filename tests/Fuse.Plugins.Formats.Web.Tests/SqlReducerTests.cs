using Fuse.Plugins.Abstractions.Options;
using Fuse.Plugins.Formats.Web.Reducers;

namespace Fuse.Plugins.Formats.Web.Tests.Reducers;

public sealed class SqlReducerTests
{
    private readonly SqlReducer _reducer = new();
    private readonly ReductionOptions _options = new();

    [Fact]
    public void SupportedExtensions_ContainsSql()
    {
        Assert.Contains(".sql", _reducer.SupportedExtensions);
    }

    [Fact]
    public void Reduce_RemovesLineAndBlockComments()
    {
        const string input = """
            -- a leading comment
            SELECT Id, Name
            /* block
               comment */
            FROM Orders;
            """;

        var result = _reducer.Reduce(input, _options);

        Assert.DoesNotContain("leading comment", result);
        Assert.DoesNotContain("block", result);
        Assert.Contains("SELECT Id, Name", result);
        Assert.Contains("FROM Orders;", result);
    }

    [Fact]
    public void Reduce_CollapsesBlankLines()
    {
        const string input = "SELECT 1;\n\n\n\nSELECT 2;";
        var result = _reducer.Reduce(input, _options);
        Assert.DoesNotContain("\n\n", result);
    }
}

public sealed class TypeScriptReductionTests
{
    private readonly JavaScriptReducer _reducer = new();
    private readonly ReductionOptions _options = new();

    [Theory]
    [InlineData(".ts")]
    [InlineData(".tsx")]
    [InlineData(".jsx")]
    public void SupportedExtensions_CoverTypeScriptFamily(string extension)
    {
        Assert.Contains(extension, _reducer.SupportedExtensions);
    }

    [Fact]
    public void Reduce_StripsCommentsFromTypeScript()
    {
        const string input = """
            // a comment
            export function add(a: number, b: number): number {
                return a + b;
            }
            """;

        var result = _reducer.Reduce(input, _options);

        Assert.DoesNotContain("a comment", result);
        Assert.Contains("add", result);
        Assert.Contains("number", result); // type annotations preserved
    }
}
