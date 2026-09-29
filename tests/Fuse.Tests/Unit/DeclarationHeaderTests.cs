using Fuse.Changes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Tests.Unit;

public class DeclarationHeaderTests
{
    [Theory]
    [InlineData("public class C : System.IDisposable { public void Dispose() { } }", "public class C : System.IDisposable")]
    [InlineData("[System.Obsolete]\npublic sealed class C<T>\n    where T : class\n{\n    int x;\n}", "[System.Obsolete] public sealed class C<T> where T : class")]
    [InlineData("public record R(int X);", "public record R(int X)")]
    [InlineData("public enum E { A, B }", "public enum E")]
    [InlineData("public interface I { void M(); }", "public interface I")]
    public void A_type_declaration_is_its_header_on_one_line(string source, string expected) =>
        Assert.Equal(expected, DeclarationHeader.Of(First<BaseTypeDeclarationSyntax>(source)));

    [Theory]
    [InlineData("class C { public int M(int a) { return a; } }", "public int M(int a)")]
    [InlineData("class C { public int M(int a) => a; }", "public int M(int a)")]
    [InlineData("interface I { int M(int a); }", "int M(int a)")]
    [InlineData("class C { public int P { get; set; } = 5; }", "public int P")]
    [InlineData("class C { public event System.EventHandler Changed { add { } remove { } } }", "public event System.EventHandler Changed")]
    public void A_member_declaration_is_its_header_without_body_or_initializer(string source, string expected) =>
        Assert.Equal(expected, DeclarationHeader.Of(First<MemberDeclarationSyntax>(source, skipTypes: true)));

    /// <summary>
    ///     A field's declaration is keyed and quoted per variable, so the header is given the variable's declarator, as
    ///     <see cref="FileDeclarations"/> gives it.
    /// </summary>
    [Theory]
    [InlineData("class C { private readonly int[] _data = new int[100]; }", "_data", "private readonly int[] _data")]
    [InlineData("class C { public long X = Compute(); static long Compute() => 1; }", "X", "public long X")]
    [InlineData("class C { public int a = 1, b = 2; }", "b", "public int b")]
    [InlineData("class C { [System.Obsolete]\n    public static\n        int Count; }", "Count", "[System.Obsolete] public static int Count")]
    [InlineData("class C { public const int Max = 10; }", "Max", "public const int Max = 10")]
    [InlineData("class C { public event System.EventHandler Changed; }", "Changed", "public event System.EventHandler Changed")]
    [InlineData("class C { public event System.EventHandler Changed, Closed; }", "Closed", "public event System.EventHandler Closed")]
    [InlineData("unsafe struct S { public fixed byte Buffer[16]; }", "Buffer", "public fixed byte Buffer[16]")]
    public void A_field_variable_is_its_declaration_s_modifiers_and_type_with_its_name(string source, string variable, string expected) =>
        Assert.Equal(expected, DeclarationHeader.Of(Parse(source).DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(v => v.Identifier.Text == variable)));

    private static T First<T>(string source, bool skipTypes = false)
        where T : SyntaxNode =>
        Parse(source)
            .DescendantNodes()
            .OfType<T>()
            .First(n => !skipTypes || n is not BaseTypeDeclarationSyntax);

    private static SyntaxNode Parse(string source) =>
        CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken);
}
