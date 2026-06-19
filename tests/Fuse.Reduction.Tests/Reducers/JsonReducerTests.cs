using Fuse.Reduction.Options;
using Fuse.Reduction.Reducers.Implementations;

namespace Fuse.Reduction.Tests.Reducers;

public sealed class JsonReducerTests
{
    private readonly JsonReducer _reducer = new();
    private readonly ReductionOptions _options = new();

    [Fact]
    public void Extension_ReturnsJson()
    {
        Assert.Equal(".json", _reducer.Extension);
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
