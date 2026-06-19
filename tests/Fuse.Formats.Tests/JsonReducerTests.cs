using Fuse.Languages.Abstractions.Options;
using Fuse.Formats.Reducers;

namespace Fuse.Formats.Tests.Reducers;

public sealed class JsonReducerTests
{
    private readonly JsonReducer _reducer = new();
    private readonly ReductionOptions _options = new();

    [Fact]
    public void SupportedExtensions_ContainsJson()
    {
        Assert.Contains(".json", _reducer.SupportedExtensions);
    }

    [Fact]
    public void Reduce_CollapsesWhitespace()
    {
        const string input = """
            {
                "name": "fuse",
                "enabled": true
            }
            """;

        var result = _reducer.Reduce(input, _options);

        Assert.Equal("{\"name\":\"fuse\",\"enabled\":true}", result);
    }
}
