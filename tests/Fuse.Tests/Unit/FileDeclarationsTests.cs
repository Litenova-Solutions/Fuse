using Fuse.Changes;
using Fuse.Changes.Model;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Tests.Unit;

public class FileDeclarationsTests
{
    private static FileDeclarations Of(string source) =>
        FileDeclarations.Of(CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken));

    private static IEnumerable<string> Signatures(FileDeclarations declarations) =>
        declarations.All.Select(d => d.Key).OfType<DeclarationKey.Member>().Select(m => m.Signature);

    [Fact]
    public void Types_are_keyed_by_namespace_containing_types_and_arity()
    {
        var keys = Of("namespace Shop { class Order { class Line<T> { } } }").All.Select(d => d.Key).ToList();
        Assert.Equal([new DeclarationKey.NamedType("Shop.Order`0"), new DeclarationKey.NamedType("Shop.Order`0.Line`1")], keys);
    }

    [Fact]
    public void A_member_is_keyed_under_its_type()
    {
        var member = Assert.IsType<DeclarationKey.Member>(Of("namespace N { class C { int Add(int a, int b) => a + b; } }").All[1].Key);
        Assert.Equal(new DeclarationKey.NamedType("N.C`0"), member.Container);
        Assert.Equal("M:Add`0( int, int)", member.Signature);
    }

    [Fact]
    public void A_static_constructor_and_the_parameterless_one_have_their_own_keys() =>
        Assert.Equal(["C:()", "C:static()"], Signatures(Of("class C { public C() { } static C() { } }")));

    [Fact]
    public void An_explicit_implementation_has_its_own_key()
    {
        var members = Of("interface I { void M(); } class C : I { public void M() { } void I.M() { } }").All
            .Select(d => d.Key).OfType<DeclarationKey.Member>().Where(m => m.Container.Name == "C`0");
        Assert.Equal(2, members.Distinct().Count());
    }

    [Fact]
    public void Each_variable_of_a_field_has_its_own_key_and_its_declarator()
    {
        var fields = Of("class C { int a, b; }").All.Where(d => d.Key is DeclarationKey.Member).ToList();
        Assert.Equal(["F:a", "F:b"], fields.Select(f => ((DeclarationKey.Member)f.Key).Signature));
        Assert.All(fields, f => Assert.IsType<VariableDeclaratorSyntax>(f.Node));
    }

    [Fact]
    public void The_first_part_of_a_partial_type_in_a_file_is_kept()
    {
        var declarations = Of("partial class C : System.IDisposable { } partial class C { void M() { } }");
        Assert.Contains("IDisposable", declarations.Find(new DeclarationKey.NamedType("C`0"))!.Surface, StringComparison.Ordinal);
        Assert.Equal(["M:M`0()"], Signatures(declarations));
    }

    [Fact]
    public void Top_level_statements_are_one_declaration_with_no_surface()
    {
        var declaration = Assert.Single(Of("System.Console.WriteLine(1); System.Console.WriteLine(2);").All);
        Assert.IsType<DeclarationKey.TopLevelStatements>(declaration.Key);
        Assert.IsType<CompilationUnitSyntax>(declaration.Node);
        Assert.Null(declaration.Surface);
    }

    [Fact]
    public void An_ordinary_using_carries_the_names_of_the_types_the_file_declares()
    {
        var directive = Assert.Single(Of("using System; namespace N { class A { } delegate void D(); }").All, d => d.Key is DeclarationKey.Using);
        Assert.Equal(["A", "D"], directive.Names);
    }
}
