using Fuse.Plugins.Languages.CSharp.Roslyn;

namespace Fuse.Plugins.Languages.CSharp.Roslyn.Tests;

public class RoslynSkeletonExtractorTests
{
    private readonly RoslynSkeletonExtractor _extractor = new();

    [Fact]
    public void ExtractSkeleton_KeepsSignaturesDropsBodies()
    {
        const string input = """
            public class OrderService
            {
                public int Charge(int amount)
                {
                    var total = amount * 2;
                    return total;
                }
            }
            """;

        var result = _extractor.ExtractSkeleton(input);

        Assert.Contains("class OrderService", result);
        Assert.Contains("Charge", result);
        Assert.DoesNotContain("var total", result);
    }

    [Fact]
    public void ExtractSkeleton_SurvivesConditionalCompilation()
    {
        // The case where the regex extractor's brace counting desynchronizes: a brace inside an inactive
        // preprocessor block. Roslyn parses it correctly and keeps every signature.
        const string input = """
            public class Holder
            {
            #if NET48
                public void Legacy() { if (true) { } }
            #endif
                public void Current() { }
                public void After() { }
            }
            """;

        var result = _extractor.ExtractSkeleton(input);

        Assert.Contains("Current", result);
        Assert.Contains("After", result);
    }

    [Fact]
    public void ExtractSkeleton_PublicApiOnly_DropsPrivateMembers()
    {
        const string input = """
            public class Svc
            {
                public void Visible() { }
                private void Hidden() { }
            }
            """;

        var result = _extractor.ExtractSkeleton(input, publicApiOnly: true);

        Assert.Contains("Visible", result);
        Assert.DoesNotContain("Hidden", result);
    }
}

public class RoslynDependencyExtractorTests
{
    private readonly RoslynDependencyExtractor _extractor = new();

    [Fact]
    public void ExtractReferencedTypes_CapturesReturnTypesAndGenerics()
    {
        const string input = """
            public class Svc
            {
                public Task<Order> CreateAsync() { return null; }
                private Repository _repo;
            }
            """;

        var refs = _extractor.ExtractReferencedTypes(input);

        Assert.Contains("Order", refs);
        Assert.Contains("Repository", refs);
    }

    [Fact]
    public void ExtractReferencedTypes_IgnoresCommentsAndStrings()
    {
        const string input = """
            public class Svc
            {
                // references PhantomType in a comment
                public void M() { var s = "AlsoPhantom"; }
            }
            """;

        var refs = _extractor.ExtractReferencedTypes(input);

        Assert.DoesNotContain("PhantomType", refs);
        Assert.DoesNotContain("AlsoPhantom", refs);
    }
}

public class RoslynTypeNameLocatorTests
{
    private readonly RoslynTypeNameLocator _locator = new();

    [Fact]
    public void ExtractDefinedTypes_FindsAllDeclaredTypes()
    {
        const string input = "public class A { } public interface IB { } public enum E { X }";

        var types = _locator.ExtractDefinedTypes(input);

        Assert.Contains("A", types);
        Assert.Contains("IB", types);
        Assert.Contains("E", types);
    }

    [Fact]
    public void ContainsTypeDefinition_RespectsExactName()
    {
        const string input = "public class OrderService { }";

        Assert.True(_locator.ContainsTypeDefinition(input, "OrderService"));
        Assert.False(_locator.ContainsTypeDefinition(input, "Order"));
    }
}

public class RoslynSymbolSliceExtractorTests
{
    private readonly RoslynSymbolSliceExtractor _extractor = new();

    [Fact]
    public void ExtractSlice_KeepsTargetMemberBodyAndStripsOthers()
    {
        const string input = """
            public class OrderService
            {
                public void Charge()
                {
                    var receipt = Compute();
                }

                public void Refund()
                {
                    var x = 99;
                }
            }
            """;

        var slice = _extractor.ExtractSlice(input, "Charge");

        Assert.NotNull(slice);
        Assert.Contains("var receipt = Compute()", slice); // target body kept
        Assert.Contains("Refund", slice);                  // sibling signature kept
        Assert.DoesNotContain("var x = 99", slice);        // sibling body stripped
    }

    [Fact]
    public void ExtractSlice_UnknownMember_ReturnsNull()
    {
        const string input = "public class A { public void M() { } }";
        Assert.Null(_extractor.ExtractSlice(input, "Missing"));
    }
}

public class RoslynOutlineExtractorTests
{
    private readonly RoslynOutlineExtractor _extractor = new();

    [Fact]
    public void ExtractOutline_AttributesMembersToTypes()
    {
        const string input = """
            public class A
            {
                public void MethodA() { }
                public int PropA { get; set; }
            }
            public enum Status { Pending, Active }
            """;

        var outline = _extractor.ExtractOutline(input);

        Assert.Equal(2, outline.Count);
        Assert.Equal("class", outline[0].Kind);
        Assert.Contains("MethodA", outline[0].Members);
        Assert.Contains("PropA", outline[0].Members);
        Assert.Equal("enum", outline[1].Kind);
        Assert.Equal(new[] { "Pending", "Active" }, outline[1].Members);
    }
}

public class RoslynSymbolChunkExtractorTests
{
    private readonly RoslynSymbolChunkExtractor _extractor = new();

    [Fact]
    public void ExtractChunks_OneChunkPerMember_WithKindNameParentAndSpan()
    {
        const string input = """
            public class OrderService
            {
                public int Total { get; set; }

                public int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """;

        var chunks = _extractor.ExtractChunks(input);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("property", chunks[0].SymbolKind);
        Assert.Equal("Total", chunks[0].SymbolName);
        Assert.Equal("OrderService", chunks[0].ParentType);

        Assert.Equal("method", chunks[1].SymbolKind);
        Assert.Equal("Add", chunks[1].SymbolName);
        Assert.Contains("return a + b;", chunks[1].Content);
        Assert.Equal(5, chunks[1].StartLine);
        Assert.Equal(8, chunks[1].EndLine);
    }

    [Fact]
    public void ExtractChunks_SurvivesConditionalCompilation()
    {
        // The case where the regex extractor desynchronizes: a brace inside an inactive preprocessor block.
        const string input = """
            public class Holder
            {
            #if NET48
                public void Legacy() { if (true) { } }
            #endif
                public void Current() { }
            }
            """;

        var chunks = _extractor.ExtractChunks(input);

        Assert.Contains(chunks, c => c.SymbolName == "Current");
    }

    [Fact]
    public void ExtractChunks_MemberBodiesParseStandalone()
    {
        const string input = """
            public class Calc
            {
                public int Compute(int n)
                {
                    var total = 0;
                    for (var i = 0; i < n; i++) { total += i; }
                    return total;
                }
            }
            """;

        var chunk = Assert.Single(_extractor.ExtractChunks(input));

        // A standalone member body must be brace-balanced so it parses on its own.
        Assert.Equal(chunk.Content.Count(c => c == '{'), chunk.Content.Count(c => c == '}'));
        Assert.EndsWith("}", chunk.Content);
    }

    [Fact]
    public void ExtractChunks_ConstructorAndEnumKinds()
    {
        const string input = """
            public class Widget
            {
                public Widget() { }
            }
            public enum Status { Pending, Active }
            """;

        var chunks = _extractor.ExtractChunks(input);

        Assert.Contains(chunks, c => c is { SymbolKind: "constructor", SymbolName: "Widget" });
        Assert.Contains(chunks, c => c is { SymbolKind: "enum-member", SymbolName: "Pending" });
    }
}
