using Fuse.Languages.Abstractions.Options;
using Fuse.Formats.Reducers;

namespace Fuse.Formats.Tests.Reducers;

public sealed class YamlReducerTests
{
    private readonly YamlReducer _reducer = new();
    private readonly ReductionOptions _options = new();

    [Fact]
    public void SupportedExtensions_ContainsYaml()
    {
        Assert.Contains(".yaml", _reducer.SupportedExtensions);
    }

    [Fact]
    public void Reduce_RemovesCommentsAndBlankLines()
    {
        const string input = """
            # config
            name: fuse

            enabled: true
            """;

        var result = _reducer.Reduce(input, _options);

        Assert.DoesNotContain("# config", result);
        Assert.Contains("name: fuse", result);
        Assert.Contains("enabled: true", result);
    }
}
