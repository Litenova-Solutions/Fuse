using Fuse.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Tests.Unit;

/// <summary>
///     Which changed declarations compile to the same code as HEAD's and select no tests. A declaration wrongly
///     dropped misses a failing test; a declaration wrongly kept only runs extra tests.
/// </summary>
public class CompiledCodeTests
{
    private static bool SameCode(string before, string after) =>
        SameCode(before, after, m => m is MethodDeclarationSyntax or PropertyDeclarationSyntax);

    private static bool SameCode(string before, string after, Func<MemberDeclarationSyntax, bool> pick)
    {
        var ct = TestContext.Current.CancellationToken;
        var (beforeModel, beforeRoot) = Bind(before, ct);
        var (afterModel, afterRoot) = Bind(after, ct);
        var beforeDecl = beforeRoot.DescendantNodes().OfType<MemberDeclarationSyntax>().First(pick);
        var afterDecl = afterRoot.DescendantNodes().OfType<MemberDeclarationSyntax>().First(pick);
        return CompiledCode.SameCode(afterModel, afterDecl, beforeModel, beforeDecl);
    }

    private static (SemanticModel Model, SyntaxNode Root) Bind(string source, CancellationToken ct)
    {
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: ct);
        var compilation = CSharpCompilation.Create(
            "CompiledCodeFixture",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location), MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return (compilation.GetSemanticModel(tree), tree.GetRoot(ct));
    }

    [Fact]
    public void Local_rename_is_same_code()
    {
        Assert.True(SameCode(
            "class C { int M(int input) { int value = input * 2; return value + 1; } }",
            "class C { int M(int input) { int result = input * 2; return result + 1; } }"));
    }

    [Fact]
    public void Var_against_the_explicit_type_is_same_code()
    {
        Assert.True(SameCode(
            "class C { int M(int input) { int value = input * 2; return value; } }",
            "class C { int M(int input) { var value = input * 2; return value; } }"));
    }

    [Fact]
    public void Expression_body_against_a_block_body_is_same_code()
    {
        Assert.True(SameCode("class C { int M(int a, int b) => a * b; }", "class C { int M(int a, int b) { return a * b; } }"));
        Assert.True(SameCode("class C { int M(int a, int b) { return a * b; } }", "class C { int M(int a, int b) => a * b; }"));
    }

    [Fact]
    public void Local_rename_in_an_accessor_is_same_code()
    {
        Assert.True(SameCode(
            "class C { int X { get { int value = 1; return value; } } }",
            "class C { int X { get { int result = 1; return result; } } }"));
    }

    [Fact]
    public void Changed_constant_is_not_same_code()
    {
        Assert.False(SameCode(
            "class C { int M() { int value = 1; return value + 1; } }",
            "class C { int M() { int value = 1; return value + 2; } }"));
    }

    [Fact]
    public void Changed_string_is_not_same_code()
    {
        Assert.False(SameCode(
            "class C { string M(string name) => \"Hello \" + name; }",
            "class C { string M(string name) => \"Hello, \" + name; }"));
    }

    [Fact]
    public void Changed_lambda_body_is_not_same_code()
    {
        Assert.False(SameCode(
            "class C { int M() { System.Func<int, int> f = x => x + 1; return f(1); } }",
            "class C { int M() { System.Func<int, int> f = x => x + 2; return f(1); } }"));
    }

    [Fact]
    public void Changed_local_function_is_not_same_code()
    {
        Assert.False(SameCode(
            "class C { int M(int v) { int Double(int x) { return x * 2; } return Double(v); } }",
            "class C { int M(int v) { int Double(int x) { return x * 3; } return Double(v); } }"));
    }

    [Fact]
    public void Changed_awaited_expression_is_not_same_code()
    {
        Assert.False(SameCode(
            "class C { async System.Threading.Tasks.Task<int> M() { await System.Threading.Tasks.Task.Delay(1); return 1; } }",
            "class C { async System.Threading.Tasks.Task<int> M() { await System.Threading.Tasks.Task.Delay(2); return 1; } }"));
    }

    [Fact]
    public void Abstract_method_has_no_code()
    {
        const string Source = "abstract class C { public abstract int M(); }";
        Assert.False(SameCode(Source, Source));
    }

    [Fact]
    public void Rename_under_nameof_is_not_same_code()
    {
        // The name is the value there, so renaming it changes what the code does.
        Assert.False(SameCode(
            "class C { string M() { int value = 1; return value.ToString() + nameof(value); } }",
            "class C { string M() { int result = 1; return result.ToString() + nameof(result); } }"));
    }

    [Fact]
    public void Parameter_default_change_is_not_same_code()
    {
        Assert.False(SameCode("class C { int M(int x = 1) => x; }", "class C { int M(int x = 2) => x; }"));
    }

    [Fact]
    public void Parameter_rename_is_not_same_code()
    {
        Assert.False(SameCode("class C { int M(int first) => first; }", "class C { int M(int second) => second; }"));
    }

    [Fact]
    public void Return_type_change_is_not_same_code()
    {
        Assert.False(SameCode("class C { int M() => 1; }", "class C { long M() => 1; }"));
    }

    [Fact]
    public void Rename_that_shadows_is_not_same_code()
    {
        // The inner use binds to the inner variable after the rename, where it bound to the outer one before.
        Assert.False(SameCode(
            "class C { int M() { int x = 1; { int y = x; return y; } } }",
            "class C { int M() { int y = 1; { int y = y; return y; } } }"));
    }

    [Fact]
    public void Field_initializer_rewrite_is_not_same_code()
    {
        // Fields never qualify: only methods and properties without initializers are compared.
        Assert.False(SameCode("class C { int f = 1 + 1; }", "class C { int f = 2; }", m => m is FieldDeclarationSyntax));
    }

    [Fact]
    public void Query_with_a_rename_is_not_same_code()
    {
        // Queries never qualify: their range variables declare nowhere the comparison reaches.
        Assert.False(SameCode(
            "using System.Linq; class C { int M(int[] xs) { var q = from x in xs select x + 1; return q.Count(); } }",
            "using System.Linq; class C { int M(int[] xs) { var q = from y in xs select y + 1; return q.Count(); } }"));
    }
}
