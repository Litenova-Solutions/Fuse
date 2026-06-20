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
