using Fuse.Analysis.Markers;
using Fuse.Reduction.Markers;

namespace Fuse.Analysis.Tests.Markers;

public class CSharpSemanticMarkerGeneratorTests
{
    private readonly CSharpSemanticMarkerGenerator _generator = new();

    [Fact]
    public void GenerateMarkers_SimpleClass_ProducesMarker()
    {
        var markers = _generator.GenerateMarkers("public class Foo { }");
        Assert.Single(markers);
        Assert.Equal("Foo", markers[0].TypeName);
        Assert.Equal("class", markers[0].Kind);
    }

    [Fact]
    public void GenerateMarkers_ClassWithInterface_PopulatesImplements()
    {
        var markers = _generator.GenerateMarkers("public class Foo : IFoo { }");
        Assert.Contains("IFoo", markers[0].Implements);
    }

    [Fact]
    public void GenerateMarkers_ClassWithConstructor_PopulatesConstructorTypes()
    {
        var markers = _generator.GenerateMarkers("""
            public class Foo
            {
                public Foo(IRepo repo, ILogger logger) { }
            }
            """);
        Assert.Contains("IRepo", markers[0].ConstructorParameterTypes);
    }

    [Fact]
    public void GenerateMarkers_Interface_ProducesMarker()
    {
        var markers = _generator.GenerateMarkers("public interface IFoo { void M(); }");
        Assert.Equal("interface", markers[0].Kind);
    }

    [Fact]
    public void GenerateMarkers_Record_ProducesMarker()
    {
        var markers = _generator.GenerateMarkers("public record Foo(int Id);");
        Assert.Equal("record", markers[0].Kind);
    }

    [Fact]
    public void GenerateMarkers_Enum_ProducesMarker()
    {
        var markers = _generator.GenerateMarkers("public enum Status { Active }");
        Assert.Equal("enum", markers[0].Kind);
    }

    [Fact]
    public void GenerateMarkers_MultipleTypes_ProducesMultipleMarkers()
    {
        var markers = _generator.GenerateMarkers("""
            public class A { }
            public class B { }
            """);
        Assert.Equal(2, markers.Count);
    }

    [Fact]
    public void GenerateMarkers_EmptyFile_ReturnsEmpty()
    {
        Assert.Empty(_generator.GenerateMarkers(string.Empty));
    }

    [Fact]
    public void SemanticMarker_ToComment_FormatsCorrectly()
    {
        var marker = new SemanticMarker(
            "OrderService",
            "class",
            ["IOrderService"],
            ["IPaymentGateway"],
            ["IPaymentGateway"]);
        var comment = marker.ToComment();
        Assert.Contains("fuse:type OrderService", comment);
        Assert.Contains("kind:class", comment);
        Assert.Contains("implements:IOrderService", comment);
    }

    [Fact]
    public void SemanticMarker_ToComment_NoneFields_OmitsNone()
    {
        var marker = new SemanticMarker("Foo", "class", [], [], []);
        var comment = marker.ToComment();
        Assert.Contains("implements:none", comment);
        Assert.Contains("depends-on:none", comment);
        Assert.Contains("constructors:none", comment);
    }
}
