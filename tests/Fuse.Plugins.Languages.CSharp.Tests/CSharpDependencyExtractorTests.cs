using Fuse.Plugins.Languages.CSharp.Dependencies;

namespace Fuse.Plugins.Languages.CSharp.Tests.Dependencies;

public class CSharpDependencyExtractorTests
{
    private readonly CSharpDependencyExtractor _extractor = new();

    [Fact]
    public void ExtractReferencedTypes_ConstructorParams_ExtractsTypes()
    {
        var result = _extractor.ExtractReferencedTypes("public class Foo { public Foo(IRepo repo) { } }");
        Assert.Contains("IRepo", result);
    }

    [Fact]
    public void ExtractReferencedTypes_PropertyTypes_ExtractsTypes()
    {
        var result = _extractor.ExtractReferencedTypes("public class Foo { public IRepo Repo { get; set; } }");
        Assert.Contains("IRepo", result);
    }

    [Fact]
    public void ExtractReferencedTypes_BaseClass_ExtractsType()
    {
        var result = _extractor.ExtractReferencedTypes("public class Foo : Bar { }");
        Assert.Contains("Bar", result);
    }

    [Fact]
    public void ExtractReferencedTypes_ImplementedInterfaces_ExtractsAll()
    {
        var result = _extractor.ExtractReferencedTypes("public class Foo : IBar, IBaz { }");
        Assert.Contains("IBar", result);
        Assert.Contains("IBaz", result);
    }

    [Fact]
    public void ExtractReferencedTypes_PrimitiveTypes_NotIncluded()
    {
        var result = _extractor.ExtractReferencedTypes("public class Foo { public string Name { get; set; } public int Count { get; set; } }");
        Assert.DoesNotContain("string", result);
        Assert.DoesNotContain("int", result);
    }

    [Fact]
    public void ExtractReferencedTypes_EmptyFile_ReturnsEmpty()
    {
        Assert.Empty(_extractor.ExtractReferencedTypes(string.Empty));
    }

    [Fact]
    public void ExtractReferencedTypes_TypeNameInComment_IsIgnored()
    {
        var result = _extractor.ExtractReferencedTypes("public class Foo { /* uses PaymentGateway */ public Foo(IRepo repo) { } }");
        Assert.Contains("IRepo", result);
        Assert.DoesNotContain("PaymentGateway", result);
    }

    [Fact]
    public void ExtractReferencedTypes_TypeNameInString_IsIgnored()
    {
        var result = _extractor.ExtractReferencedTypes("public class Foo { public Foo(IRepo repo) { var s = \"CatalogItem\"; } }");
        Assert.Contains("IRepo", result);
        Assert.DoesNotContain("CatalogItem", result);
    }
}
