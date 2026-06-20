using Fuse.Plugins.Languages.CSharp.Dependencies;

namespace Fuse.Plugins.Languages.CSharp.Tests.Dependencies;

public class CSharpSourceSanitizerTests
{
    [Fact]
    public void Sanitize_LineComment_IsBlanked()
    {
        var result = CSharpSourceSanitizer.Sanitize("var x = 1; // see OrderService\n");
        Assert.DoesNotContain("OrderService", result);
        Assert.Contains("var x = 1;", result);
    }

    [Fact]
    public void Sanitize_BlockComment_IsBlanked()
    {
        var result = CSharpSourceSanitizer.Sanitize("/* returns PaymentGateway */ int y;");
        Assert.DoesNotContain("PaymentGateway", result);
        Assert.Contains("int y;", result);
    }

    [Fact]
    public void Sanitize_RegularString_IsBlanked()
    {
        var result = CSharpSourceSanitizer.Sanitize("var s = \"new CatalogItem()\";");
        Assert.DoesNotContain("CatalogItem", result);
    }

    [Fact]
    public void Sanitize_VerbatimString_WithEscapedQuotes_IsBlanked()
    {
        var result = CSharpSourceSanitizer.Sanitize("var s = @\"a \"\"InvoiceService\"\" b\"; int z;");
        Assert.DoesNotContain("InvoiceService", result);
        Assert.Contains("int z;", result);
    }

    [Fact]
    public void Sanitize_InterpolatedString_BlanksHoles()
    {
        var result = CSharpSourceSanitizer.Sanitize("var s = $\"x {LedgerAccount} y\";");
        Assert.DoesNotContain("LedgerAccount", result);
    }

    [Fact]
    public void Sanitize_RawString_IsBlanked()
    {
        var result = CSharpSourceSanitizer.Sanitize("var s = \"\"\"\nclass HiddenType {}\n\"\"\";");
        Assert.DoesNotContain("HiddenType", result);
    }

    [Fact]
    public void Sanitize_PreservesLength()
    {
        const string source = "var s = \"abc\"; // comment";
        var result = CSharpSourceSanitizer.Sanitize(source);
        Assert.Equal(source.Length, result.Length);
    }

    [Fact]
    public void Sanitize_KeepsRealCode()
    {
        var result = CSharpSourceSanitizer.Sanitize("public class Foo : Bar { }");
        Assert.Contains("class Foo", result);
        Assert.Contains("Bar", result);
    }
}
