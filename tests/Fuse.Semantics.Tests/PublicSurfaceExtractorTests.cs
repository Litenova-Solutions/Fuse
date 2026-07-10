using Fuse.Semantics;
using Xunit;

namespace Fuse.Semantics.Tests;

// T2: PublicSurfaceExtractor computes effective member accessibility (which SyntaxSymbolExtractor leaves unset),
// so the API-delta can compare member surfaces. These pin the accessibility rules that make a member count as
// public API: container defaults, nested-type reachability, interface members, and overload distinctness.
public sealed class PublicSurfaceExtractorTests
{
    [Fact]
    public void A_public_method_of_a_public_class_is_on_the_surface()
    {
        var symbols = PublicSurfaceExtractor.Extract("A.cs", "public class A { public void Foo() { } }");
        Assert.Contains(symbols, s => s.Name == "Foo" && s.Kind == "method");
    }

    [Fact]
    public void A_private_member_is_not_on_the_surface()
    {
        var symbols = PublicSurfaceExtractor.Extract("A.cs", "public class A { private void Hidden() { } void AlsoHidden() { } }");
        Assert.DoesNotContain(symbols, s => s.Name is "Hidden" or "AlsoHidden");
    }

    [Fact]
    public void An_implicitly_private_class_member_defaults_off_the_surface()
    {
        // No accessibility modifier on a class member means private, so it is not public API.
        var symbols = PublicSurfaceExtractor.Extract("A.cs", "public class A { void Implicit() { } }");
        Assert.DoesNotContain(symbols, s => s.Name == "Implicit");
    }

    [Fact]
    public void An_interface_member_is_public_by_default()
    {
        var symbols = PublicSurfaceExtractor.Extract("A.cs", "public interface IA { void Contract(); }");
        Assert.Contains(symbols, s => s.Name == "Contract");
    }

    [Fact]
    public void A_public_member_of_an_internal_class_is_not_on_the_surface()
    {
        var symbols = PublicSurfaceExtractor.Extract("A.cs", "internal class A { public void Foo() { } }");
        Assert.Empty(symbols);
    }

    [Fact]
    public void A_public_nested_type_inside_an_internal_type_is_not_reachable()
    {
        var symbols = PublicSurfaceExtractor.Extract("A.cs", "internal class Outer { public class Inner { public void Foo() { } } }");
        Assert.Empty(symbols);
    }

    [Fact]
    public void A_protected_member_is_on_the_surface()
    {
        var symbols = PublicSurfaceExtractor.Extract("A.cs", "public class A { protected void Extend() { } }");
        Assert.Contains(symbols, s => s.Name == "Extend");
    }

    [Fact]
    public void Overloads_are_kept_distinct_by_parameter_types()
    {
        var symbols = PublicSurfaceExtractor.Extract("A.cs",
            "public class A { public void Foo(int x) { } public void Foo(string s) { } }");
        var foos = symbols.Where(s => s.Name == "Foo").ToList();
        Assert.Equal(2, foos.Count);
        Assert.Equal(2, foos.Select(f => f.FullyQualifiedName).Distinct().Count());
    }

    [Fact]
    public void A_public_property_reports_its_accessor_shape()
    {
        var symbols = PublicSurfaceExtractor.Extract("A.cs", "public class A { public int Value { get; set; } }");
        var property = Assert.Single(symbols, s => s.Name == "Value");
        Assert.Contains("get", property.Signature);
        Assert.Contains("set", property.Signature);
    }

    [Fact]
    public void A_generic_and_a_non_generic_type_of_the_same_name_are_distinct_identities()
    {
        var symbols = PublicSurfaceExtractor.Extract("A.cs",
            "public interface IFoo { } public interface IFoo<T> { }");
        var types = symbols.Where(s => s.Name == "IFoo").ToList();
        Assert.Equal(2, types.Count);
        // The arity marker keeps them apart, so neither hides the other in a surface diff.
        Assert.Equal(2, types.Select(t => t.FullyQualifiedName).Distinct().Count());
        Assert.Contains(types, t => t.FullyQualifiedName.EndsWith("IFoo`1", StringComparison.Ordinal));
    }

    [Fact]
    public void Whitespace_differences_normalize_to_the_same_signature()
    {
        var tight = PublicSurfaceExtractor.Extract("A.cs", "public class A { public void Foo(int x){} }");
        var loose = PublicSurfaceExtractor.Extract("A.cs", "public class A {\n    public   void   Foo(int x)\n    {\n    }\n}");
        var tightFoo = Assert.Single(tight, s => s.Name == "Foo");
        var looseFoo = Assert.Single(loose, s => s.Name == "Foo");
        Assert.Equal(tightFoo.Signature, looseFoo.Signature);
    }
}
