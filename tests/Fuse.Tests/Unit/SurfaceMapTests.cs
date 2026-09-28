using Fuse.Check;
using Microsoft.CodeAnalysis.CSharp;

namespace Fuse.Tests.Unit;

public class SurfaceMapTests
{
    private static List<SurfaceChange> Changes(string before, string after) =>
        SurfaceMap.Changes(
            SurfaceMap.Compute(CSharpSyntaxTree.ParseText(before, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken)),
            SurfaceMap.Compute(CSharpSyntaxTree.ParseText(after, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken)));

    private static IEnumerable<string> Names(IEnumerable<SurfaceChange> changes) =>
        changes.SelectMany(c => (c.After ?? c.Before)!.Names).Distinct().Order(StringComparer.Ordinal);

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
        Assert.Contains(changes, c => c.After is null && c.Key.Contains(".Add", StringComparison.Ordinal));
        Assert.Contains(changes, c => c.Before is null && c.Key.Contains(".Plus", StringComparison.Ordinal));
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
    public void Field_initializer_is_not_part_of_the_surface() =>
        Assert.Empty(Changes("class C { public int X = 1; }", "class C { public int X = 2; }"));

    [Fact]
    public void Base_list_change_changes_the_type_header()
    {
        var change = Assert.Single(Changes("class B {} class C { }", "class B {} class C : B { }"));
        Assert.StartsWith("T:", change.Key, StringComparison.Ordinal);
        Assert.NotNull(change.Before);
        Assert.NotNull(change.After);
    }

    [Fact]
    public void Operator_is_part_of_the_surface() =>
        Assert.Contains(Changes("struct V { }", "struct V { public static V operator +(V a, V b) => a; }"), c => c.Key.StartsWith("O:", StringComparison.Ordinal));

    [Fact]
    public void Global_using_is_part_of_the_surface() =>
        Assert.Contains(Changes("global using System;", "global using System.Text;"), c => c.Key.StartsWith("G:", StringComparison.Ordinal));

    [Fact]
    public void Added_type_is_an_addition()
    {
        var change = Assert.Single(Changes("namespace N { class A {} }", "namespace N { class A {} class B {} }"));
        Assert.Null(change.Before);
        Assert.Equal(["B"], change.After!.Names);
    }

    [Fact]
    public void Enum_member_value_is_part_of_the_surface() =>
        Assert.Equal(["A", "E"], Names(Changes("enum E { A = 1 }", "enum E { A = 2 }")));

    [Fact]
    public void Overloads_are_distinguished()
    {
        var change = Assert.Single(Changes("class C { void M(int a) {} void M(string s) {} }", "class C { void M(int a) {} }"));
        Assert.Contains("string", change.Key, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public class C : System.IDisposable { public void Dispose() { } }", "public class C : System.IDisposable")]
    [InlineData("[System.Obsolete]\npublic sealed class C<T>\n    where T : class\n{\n    int x;\n}", "[System.Obsolete] public sealed class C<T> where T : class")]
    [InlineData("public record R(int X);", "public record R(int X)")]
    [InlineData("public enum E { A, B }", "public enum E")]
    [InlineData("public interface I { void M(); }", "public interface I")]
    public void A_type_declaration_is_its_header_on_one_line(string source, string expected) =>
        Assert.Equal(expected, SurfaceMap.Declaration(First<Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax>(source)));

    [Theory]
    [InlineData("class C { public int M(int a) { return a; } }", "public int M(int a)")]
    [InlineData("class C { public int M(int a) => a; }", "public int M(int a)")]
    [InlineData("interface I { int M(int a); }", "int M(int a)")]
    [InlineData("class C { public int P { get; set; } = 5; }", "public int P")]
    [InlineData("class C { private readonly int[] _data = new int[100]; }", "private readonly int[] _data")]
    [InlineData("class C { public event System.EventHandler Changed; }", "public event System.EventHandler Changed")]
    [InlineData("class C { public event System.EventHandler Changed { add { } remove { } } }", "public event System.EventHandler Changed")]
    public void A_member_declaration_is_its_header_without_body_or_initializer(string source, string expected) =>
        Assert.Equal(expected, SurfaceMap.Declaration(First<Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax>(source, skipTypes: true)));

    private static T First<T>(string source, bool skipTypes = false)
        where T : Microsoft.CodeAnalysis.SyntaxNode =>
        CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes()
            .OfType<T>()
            .First(n => !skipTypes || n is not Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax);
}
