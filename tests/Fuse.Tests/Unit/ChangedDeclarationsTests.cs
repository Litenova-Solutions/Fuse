using Fuse.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Tests.Unit;

public class ChangedDeclarationsTests
{
    private static List<string> Changed(string? before, string after)
    {
        var ct = TestContext.Current.CancellationToken;
        var oldRoot = before is null ? null : CSharpSyntaxTree.ParseText(before, cancellationToken: ct).GetRoot(ct);
        var newRoot = CSharpSyntaxTree.ParseText(after, cancellationToken: ct).GetRoot(ct);
        return ChangedDeclarations.Find(oldRoot, newRoot).Select(Describe).ToList();
    }

    private static string Describe(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => "method " + m.Identifier.Text,
        BaseTypeDeclarationSyntax t => "type " + t.Identifier.Text,
        PropertyDeclarationSyntax p => "property " + p.Identifier.Text,
        FieldDeclarationSyntax f => "field " + f.Declaration.Variables[0].Identifier.Text,
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
}
