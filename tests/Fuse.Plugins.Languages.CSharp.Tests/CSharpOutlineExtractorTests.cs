using Fuse.Plugins.Languages.CSharp.Outline;

namespace Fuse.Plugins.Languages.CSharp.Tests.Outline;

public class CSharpOutlineExtractorTests
{
    private readonly CSharpOutlineExtractor _extractor = new();

    [Fact]
    public void ExtractOutline_ReturnsTypeWithKindAndName()
    {
        const string input = """
            public class OrderService
            {
            }
            """;

        var outline = _extractor.ExtractOutline(input);

        var symbol = Assert.Single(outline);
        Assert.Equal("class", symbol.Kind);
        Assert.Equal("OrderService", symbol.Name);
    }

    [Fact]
    public void ExtractOutline_CapturesMethodsAndProperties()
    {
        const string input = """
            public class OrderService
            {
                public string Name { get; set; }
                public Task<Order> CreateAsync(string name) { return null; }
                public int Total => 0;
            }
            """;

        var symbol = Assert.Single(_extractor.ExtractOutline(input));

        Assert.Contains("Name", symbol.Members);
        Assert.Contains("CreateAsync", symbol.Members);
        Assert.Contains("Total", symbol.Members);
    }

    [Fact]
    public void ExtractOutline_DoesNotCaptureMethodBodyLocals()
    {
        const string input = """
            public class Foo
            {
                public void Bar()
                {
                    var helper = Compute(1);
                    if (helper > 0) { Compute(2); }
                }
            }
            """;

        var symbol = Assert.Single(_extractor.ExtractOutline(input));

        Assert.Contains("Bar", symbol.Members);
        // Calls inside the method body live at a deeper brace depth and must not appear as members.
        Assert.DoesNotContain("Compute", symbol.Members);
        Assert.DoesNotContain("if", symbol.Members);
    }

    [Fact]
    public void ExtractOutline_CapturesEnumMembers()
    {
        const string input = """
            public enum Status
            {
                Pending,
                Active = 2,
                Closed
            }
            """;

        var symbol = Assert.Single(_extractor.ExtractOutline(input));

        Assert.Equal("enum", symbol.Kind);
        Assert.Equal(new[] { "Pending", "Active", "Closed" }, symbol.Members);
    }

    [Fact]
    public void ExtractOutline_AttributesMembersToTheirType()
    {
        const string input = """
            public class A
            {
                public void MethodA() { }
            }

            public interface IB
            {
                void MethodB();
            }
            """;

        var outline = _extractor.ExtractOutline(input);

        Assert.Equal(2, outline.Count);
        Assert.Contains("MethodA", outline[0].Members);
        Assert.DoesNotContain("MethodB", outline[0].Members);
        Assert.Equal("interface", outline[1].Kind);
        Assert.Contains("MethodB", outline[1].Members);
    }

    [Fact]
    public void ExtractOutline_IgnoresBracesInStringLiterals()
    {
        const string input = """
            public class Templater
            {
                public string Render() { return "{ not real braces }"; }
                public void After() { }
            }
            """;

        var symbol = Assert.Single(_extractor.ExtractOutline(input));

        Assert.Contains("Render", symbol.Members);
        Assert.Contains("After", symbol.Members);
    }

    [Fact]
    public void ExtractOutline_EmptyContent_ReturnsEmpty()
    {
        Assert.Empty(_extractor.ExtractOutline(string.Empty));
    }

    [Fact]
    public void SupportedExtensions_IsCSharp()
    {
        Assert.Contains(".cs", _extractor.SupportedExtensions);
    }
}
