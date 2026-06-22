using Fuse.Plugins.Abstractions.Options;
using Fuse.Plugins.Languages.CSharp.Reducers;

namespace Fuse.Plugins.Languages.CSharp.Tests.Reducers;

public sealed class CSharpReducerTests
{
    private readonly CSharpReducer _reducer = new();
    private readonly ReductionOptions _options = new(level: ReductionLevel.Standard);

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

    [Fact]
    public void Reduce_AggressiveMode_RemovesNoiseAttributes()
    {
        const string input = """
            [DebuggerDisplay("x")]
            [GeneratedCode("tool", "1.0")]
            public class Widget
            {
                public int Id { get; set; }
            }
            """;

        var result = _reducer.Reduce(input, new ReductionOptions(level: ReductionLevel.Aggressive));

        Assert.DoesNotContain("DebuggerDisplay", result);
        Assert.DoesNotContain("GeneratedCode", result);
        Assert.Contains("public class Widget", result);
    }

    private static readonly ReductionOptions Aggressive = new(level: ReductionLevel.Aggressive);

    [Fact]
    public void Reduce_Aggressive_PreservesTripleQuotedRawStringJson()
    {
        // The embedded JSON contains structural punctuation (: , [ ]) surrounded by spaces. Whitespace
        // compression would collapse those spaces if the raw literal were not masked.
        const string input = """"
            public class C
            {
                public string Json() => """
            { "a": 1, "b": [2, 3] }
            """;
            }
            """";

        var result = _reducer.Reduce(input, Aggressive);

        Assert.Contains("{ \"a\": 1, \"b\": [2, 3] }", result);
    }

    [Fact]
    public void Reduce_Aggressive_PreservesFourQuoteRawStringContainingTripleQuotes()
    {
        // A four-quote raw literal whose body contains a literal """ sequence and spaced punctuation.
        const string input = """""
            public class C
            {
                public string Doc() => """"
            see """ x : y , z """
            """";
            }
            """"";

        var result = _reducer.Reduce(input, Aggressive);

        Assert.Contains("see \"\"\" x : y , z \"\"\"", result);
    }

    [Fact]
    public void Reduce_Aggressive_PreservesInterpolatedRawString()
    {
        const string input = """"
            public class C
            {
                public string Sql(int id) => $"""
            SELECT * FROM t WHERE id = {id} ;
            """;
            }
            """";

        var result = _reducer.Reduce(input, Aggressive);

        Assert.Contains("SELECT * FROM t WHERE id = {id} ;", result);
    }

    [Fact]
    public void Reduce_Aggressive_DoesNotTreatSlashesInsideRawStringAsComment()
    {
        const string input = """"
            public class C
            {
                public string Url() => """
            https://example.com/path // not a comment
            """;
            }
            """";

        var result = _reducer.Reduce(input, Aggressive);

        Assert.Contains("https://example.com/path // not a comment", result);
    }
}
