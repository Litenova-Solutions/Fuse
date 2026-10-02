using Fuse.Changes;
using Fuse.Changes.Model;
using Microsoft.CodeAnalysis.CSharp;

namespace Fuse.Tests.Unit;

public class SurfaceDiffTests
{
    private static FileChanges Compare(string before, string after) =>
        SurfaceDiff.Compare(
            FileDeclarations.Of(CSharpSyntaxTree.ParseText(before, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken)),
            FileDeclarations.Of(CSharpSyntaxTree.ParseText(after, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken)));

    private static IReadOnlyList<DeclarationChange> Changes(string before, string after) => Compare(before, after).Changes;

    private static IEnumerable<string> Names(IEnumerable<DeclarationChange> changes) =>
        changes.SelectMany(c => c.Names).Distinct().Order(StringComparer.Ordinal);

    private static string? Signature(DeclarationChange change) => (change.Key as DeclarationKey.Member)?.Signature;

    [Fact]
    public void Body_edit_changes_nothing() =>
        Assert.Empty(Changes("class C { public int M() => 1; }", "class C { public int M() { return 2; } }"));

    [Fact]
    public void Comments_and_formatting_change_nothing() =>
        Assert.Empty(Changes("class C { public int M(int a) => a; }", "class C\n{\n    // doc\n    public int M( int a ) => a;\n}"));

    [Fact]
    public void Renamed_method_is_a_removal_and_an_addition()
    {
        var changes = Changes("class C { public int Add() => 1; }", "class C { public int Plus() => 1; }");
        Assert.Contains(changes, c => c is DeclarationChange.Removed && Signature(c)!.StartsWith("M:Add", StringComparison.Ordinal));
        Assert.Contains(changes, c => c is DeclarationChange.Added && Signature(c)!.StartsWith("M:Plus", StringComparison.Ordinal));
        Assert.Equal(["Add", "C", "Plus"], Names(changes));
    }

    [Fact]
    public void Parameter_change_changes_the_method() =>
        Assert.Contains("M", Names(Changes("class C { public void M(int a) {} }", "class C { public void M(long a) {} }")));

    [Fact]
    public void Accessibility_change_changes_the_method() =>
        Assert.Contains("M", Names(Changes("class C { public void M() {} }", "class C { internal void M() {} }")));

    [Fact]
    public void Constant_value_is_part_of_the_surface() =>
        Assert.Contains("X", Names(Changes("class C { public const int X = 1; }", "class C { public const int X = 2; }")));

    [Fact]
    public void Line_endings_inside_a_multi_line_constant_change_nothing()
    {
        // A checkout with core.autocrlf writes CRLF where HEAD has LF, inside a raw or verbatim string as anywhere else.
        const string Lf = "class C { public const string X = \"\"\"\n    a\n    b\n    \"\"\"; public const string Y = @\"a\nb\"; }";
        Assert.Empty(Changes(Lf, Lf.Replace("\n", "\r\n", StringComparison.Ordinal)));
    }

    [Fact]
    public void Field_initializer_is_not_part_of_the_surface() =>
        Assert.Empty(Changes("class C { public int X = 1; }", "class C { public int X = 2; }"));

    [Fact]
    public void Base_list_change_changes_the_type_header()
    {
        var change = Assert.Single(Changes("class B {} class C { }", "class B {} class C : B { }"));
        Assert.IsType<DeclarationKey.NamedType>(change.Key);
        var changed = Assert.IsType<DeclarationChange.Changed>(change);
        Assert.Equal("class C", changed.Before);
        Assert.Equal("class C : B", changed.After);
    }

    [Fact]
    public void Operator_is_part_of_the_surface() =>
        Assert.Contains(Changes("struct V { }", "struct V { public static V operator +(V a, V b) => a; }"), c => Signature(c)?.StartsWith("O:", StringComparison.Ordinal) == true);

    [Fact]
    public void Global_using_is_part_of_the_surface() =>
        Assert.Contains(Changes("global using System;", "global using System.Text;"), c => c.Key is DeclarationKey.GlobalUsing);

    [Fact]
    public void Added_type_is_an_addition()
    {
        var change = Assert.Single(Changes("namespace N { class A {} }", "namespace N { class A {} class B {} }"));
        var added = Assert.IsType<DeclarationChange.Added>(change);
        Assert.Equal(["B"], added.Names);
    }

    [Fact]
    public void Enum_member_value_is_part_of_the_surface() =>
        Assert.Equal(["A", "E"], Names(Changes("enum E { A = 1 }", "enum E { A = 2 }")));

    [Fact]
    public void Overloads_are_distinguished()
    {
        var change = Assert.Single(Changes("class C { void M(int a) {} void M(string s) {} }", "class C { void M(int a) {} }"));
        Assert.Contains("string", Signature(change), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("class C { }", "public class C { }")]
    [InlineData("class B {} class C { }", "class B {} class C : B { }")]
    [InlineData("delegate void D();", "delegate void D(int a);")]
    [InlineData("class C { delegate void D(); }", "class C { }")]
    [InlineData("global using System;", "global using System.Text;")]
    [InlineData("[assembly: System.CLSCompliant(true)]", "[assembly: System.CLSCompliant(false)]")]
    public void A_change_no_reference_search_can_bound_is_broad(string before, string after) =>
        Assert.True(Compare(before, after).HasBroadChange);

    [Theory]
    [InlineData("class C { public void M(int a) {} }", "class C { public void M(long a) {} }")]
    [InlineData("class C { }", "class C { } class D { }")]
    [InlineData("class C { }", "")]
    [InlineData("using System; class C { }", "class C { }")]
    [InlineData("class C { public const int X = 1; }", "class C { public const int X = 2; }")]
    public void A_member_change_or_an_added_or_removed_type_is_not_broad(string before, string after) =>
        Assert.False(Compare(before, after).HasBroadChange);

    [Fact]
    public void A_static_constructor_beside_a_parameterless_one_is_an_addition()
    {
        var change = Assert.Single(Changes("class C { public C() {} }", "class C { public C() {} static C() {} }"));
        Assert.IsType<DeclarationChange.Added>(change);
        Assert.Equal("C:static()", Signature(change));
    }

    [Fact]
    public void A_receiver_change_in_a_later_extension_block_is_a_change()
    {
        const string Before = "static class X { extension(string s) { public int Len => s.Length; } extension(int i) { public int Twice() => i * 2; } }";
        var changes = Changes(Before, Before.Replace("extension(int i)", "extension(long i)", StringComparison.Ordinal));
        // The block for int is gone with its member, and a block for long takes its place.
        Assert.Contains(changes, c => c is DeclarationChange.Removed { Key: DeclarationKey.NamedType { Name: "X`0.extension`0( int)" } });
        Assert.Contains(changes, c => c is DeclarationChange.Removed && Signature(c) == "M:Twice`0()");
        Assert.Contains(changes, c => c is DeclarationChange.Added { Key: DeclarationKey.NamedType { Name: "X`0.extension`0( long)" } });
    }

    [Fact]
    public void A_header_change_in_a_later_part_of_a_partial_type_is_a_broad_change()
    {
        const string Before = "public partial class P { } public partial class P : System.IDisposable { public void Dispose() { } }";
        var compared = Compare(Before, Before.Replace(" : System.IDisposable", "", StringComparison.Ordinal));
        var change = Assert.Single(compared.Changes);
        Assert.IsType<DeclarationChange.Changed>(change);
        Assert.True(compared.HasBroadChange);
    }

    [Fact]
    public void Top_level_statements_and_a_finalizer_are_not_part_of_the_surface() =>
        Assert.Empty(Changes("System.Console.WriteLine(1); class C { }", "System.Console.WriteLine(2); class C { ~C() {} }"));
}
