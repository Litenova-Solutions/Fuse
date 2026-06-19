using Fuse.Languages.Abstractions.Options;
using Fuse.Languages.CSharp.Reducers;

namespace Fuse.Languages.CSharp.Tests.Reducers;

public sealed class CSharpReducerTests
{
    private readonly CSharpReducer _reducer = new();
    private readonly ReductionOptions _options = new(removeCSharpComments: true, removeCSharpUsings: true);

    [Fact]
    public void SupportedExtensions_ContainsCs()
    {
        Assert.Contains(".cs", _reducer.SupportedExtensions);
    }

    [Fact]
    public void Reduce_RemovesCommentsAndUsings()
    {
        const string input = """
            using System;

            namespace App;

            // greeting helper
            public class Greeter
            {
                public string Hello() => "hi";
            }
            """;

        var result = _reducer.Reduce(input, _options);

        Assert.DoesNotContain("using System", result);
        Assert.DoesNotContain("// greeting", result);
        Assert.Contains("public class Greeter", result);
    }
}
