using Fuse.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Tests.Unit;

/// <summary>
///     A rename that changes a name the code reads as a value is not same code: an inferred member name or a captured
///     argument. An explicit member name still is.
/// </summary>
public class CompiledCodeNameValueTests
{
    private static bool SameCode(string before, string after, string method = "M")
    {
        var ct = TestContext.Current.CancellationToken;
        var (beforeModel, beforeRoot) = Bind(before, ct);
        var (afterModel, afterRoot) = Bind(after, ct);
        var beforeDecl = beforeRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().First(m => m.Identifier.Text == method);
        var afterDecl = afterRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().First(m => m.Identifier.Text == method);
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
    public void Rename_of_an_inferred_anonymous_member_is_not_same_code()
    {
        Assert.False(SameCode(
            "class C { object M() { int value = 1; return new { value }; } }",
            "class C { object M() { int result = 1; return new { result }; } }"));
    }

    [Fact]
    public void Rename_of_an_inferred_tuple_element_is_not_same_code()
    {
        Assert.False(SameCode(
            "class C { object M() { int value = 1; return (value, 2); } }",
            "class C { object M() { int result = 1; return (result, 2); } }"));
    }

    [Fact]
    public void Rename_of_a_captured_argument_is_not_same_code()
    {
        const string Log = "void Log(object? value, [System.Runtime.CompilerServices.CallerArgumentExpression(\"value\")] string? name = null) { }";
        Assert.False(SameCode(
            $"class C {{ {Log} int M() {{ int value = 1; Log(value); return 1; }} }}",
            $"class C {{ {Log} int M() {{ int result = 1; Log(result); return 1; }} }}"));
    }

    [Fact]
    public void Rename_with_an_explicit_member_name_is_same_code()
    {
        Assert.True(SameCode(
            "class C { object M() { int value = 1; return new { X = value }; } }",
            "class C { object M() { int result = 1; return new { X = result }; } }"));
        Assert.True(SameCode(
            "class C { object M() { int value = 1; return (x: value, y: 2); } }",
            "class C { object M() { int result = 1; return (x: result, y: 2); } }"));
    }

    [Fact]
    public void Rename_of_an_uncaptured_argument_is_same_code()
    {
        const string Log = "void Log(object? value, [System.Runtime.CompilerServices.CallerArgumentExpression(\"value\")] string? name = null) { }";
        Assert.True(SameCode(
            $"class C {{ {Log} int M() {{ int other = 1; Log(0, \"x\"); return other; }} }}",
            $"class C {{ {Log} int M() {{ int renamed = 1; Log(0, \"x\"); return renamed; }} }}"));
    }
}
