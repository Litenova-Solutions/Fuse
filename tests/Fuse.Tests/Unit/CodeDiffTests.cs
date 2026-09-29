using Fuse.Changes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Tests.Unit;

public class CodeDiffTests
{
    private static List<string> Changed(string? before, string after)
    {
        var ct = TestContext.Current.CancellationToken;
        var oldRoot = before is null ? null : CSharpSyntaxTree.ParseText(before, cancellationToken: ct).GetRoot(ct);
        var newRoot = CSharpSyntaxTree.ParseText(after, cancellationToken: ct).GetRoot(ct);
        return CodeDiff.Find(oldRoot, newRoot).Select(Describe).ToList();
    }

    private static string Describe(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => "method " + m.Identifier.Text,
        BaseTypeDeclarationSyntax t => "type " + t.Identifier.Text,
        DelegateDeclarationSyntax d => "delegate " + d.Identifier.Text,
        PropertyDeclarationSyntax p => "property " + p.Identifier.Text,
        FieldDeclarationSyntax f => "field " + string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.Text)),
        CompilationUnitSyntax => "top-level",
        _ => node.Kind().ToString(),
    };

    [Fact]
    public void Body_change_marks_only_that_method()
    {
        Assert.Equal(["method B"], Changed("class C { int A() => 1; int B() => 2; }", "class C { int A() => 1; int B() => 3; }"));
    }

    [Fact]
    public void Formatting_change_marks_nothing()
    {
        Assert.Empty(Changed("class C { int A() => 1; }", "class C\n{\n    int A()   =>   1;\n}"));
    }

    [Fact]
    public void Added_member_is_marked()
    {
        Assert.Equal(["method B"], Changed("class C { int A() => 1; }", "class C { int A() => 1; int B() => 2; }"));
    }

    [Fact]
    public void Removed_member_marks_its_type()
    {
        Assert.Equal(["type C"], Changed("class C { int A() => 1; int B() => 2; }", "class C { int A() => 1; }"));
    }

    [Fact]
    public void Header_change_marks_the_type_but_member_change_does_not()
    {
        Assert.Equal(["type C"], Changed("class C { int A() => 1; }", "sealed class C { int A() => 1; }"));
    }

    [Fact]
    public void Using_change_marks_every_type_in_the_file()
    {
        Assert.Equal(["type A", "type B"], Changed("using System;\nclass A {}\nclass B {}", "using System.Text;\nclass A {}\nclass B {}"));
    }

    [Fact]
    public void Top_level_statement_change_is_marked()
    {
        Assert.Equal(["top-level"], Changed("System.Console.WriteLine(1);", "System.Console.WriteLine(2);"));
    }

    [Fact]
    public void New_file_marks_everything()
    {
        Assert.Equal(["type C", "method A"], Changed(null, "class C { int A() => 1; }"));
    }

    [Fact]
    public void A_type_is_followed_by_its_members_before_its_nested_types()
    {
        Assert.Equal(["type C", "method A", "method B", "type D", "method X"], Changed(null, "class C { void A() {} class D { void X() {} } void B() {} }"));
    }

    [Fact]
    public void Changed_variable_marks_its_whole_field_declaration()
    {
        Assert.Equal(["field a,b"], Changed("class C { int a = 1, b = 2; }", "class C { int a = 1, b = 3; }"));
    }

    [Fact]
    public void Changing_the_kind_of_a_type_marks_the_type()
    {
        Assert.Equal(["type P"], Changed("class P { int X; }", "struct P { int X; }"));
    }

    [Fact]
    public void Namespace_level_delegate_change_is_marked()
    {
        Assert.Equal(["delegate D"], Changed("delegate void D(); class C {}", "delegate void D(int a); class C {}"));
    }

    [Fact]
    public void Explicit_implementation_is_told_apart_from_a_member_with_the_same_signature()
    {
        const string Before = "interface I { void M(); } class C : I { public void M() { } void I.M() { } }";
        var after = Before.Replace("void I.M() { }", "void I.M() { M(); }", StringComparison.Ordinal);
        var node = Assert.Single(CodeDiff.Find(Parse(Before), Parse(after)));
        Assert.NotNull(Assert.IsType<MethodDeclarationSyntax>(node).ExplicitInterfaceSpecifier);
    }

    [Fact]
    public void Parameter_modifier_change_is_a_new_member_and_marks_its_type()
    {
        Assert.Equal(["method M", "type C"], Changed("class C { void M(int a) {} }", "class C { void M(ref int a) {} }"));
    }

    [Fact]
    public void Whitespace_between_parameter_modifiers_marks_nothing()
    {
        Assert.Empty(Changed("static class C { static void M(this ref int a) {} }", "static class C { static void M(this  ref int a) {} }"));
    }

    [Fact]
    public void A_body_edit_to_a_partial_method_s_implementation_in_the_same_file_marks_the_implementation()
    {
        const string Before = "partial class C { partial void M(); partial void M() { A(); } void A() {} }";
        var after = Before.Replace("{ A(); }", "{ A(); A(); }", StringComparison.Ordinal);
        var node = Assert.IsType<MethodDeclarationSyntax>(Assert.Single(CodeDiff.Find(Parse(Before), Parse(after))));
        Assert.NotNull(node.Body);
    }

    private static SyntaxNode Parse(string source) =>
        CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken);
}
