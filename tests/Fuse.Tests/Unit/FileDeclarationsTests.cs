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
    public void The_parts_of_a_partial_type_in_a_file_are_one_declaration_whose_surface_holds_every_header()
    {
        var declarations = Of("partial class C { } partial class C : System.IDisposable { void M() { } }");
        var type = declarations.Find(new DeclarationKey.NamedType("C`0"))!;
        // The second part's base list is part of the surface, so removing it from that part is a change.
        Assert.Contains("IDisposable", type.Surface, StringComparison.Ordinal);
        Assert.Equal(2, type.Parts.Count);
        Assert.Same(type.Parts[0], type.Node);
        Assert.Single(declarations.All, d => d.Key is DeclarationKey.NamedType);
        Assert.Equal(["M:M`0()"], Signatures(declarations));
    }

    [Fact]
    public void Each_extension_block_is_keyed_by_its_receiver_and_carries_its_class_s_name()
    {
        var declarations = Of("static class X { extension(string s) { public int Len => s.Length; } extension<T>(System.Collections.Generic.List<T> list) { public int Twice() => 2; } }");
        var blocks = declarations.All.Where(d => d.Node is ExtensionBlockDeclarationSyntax).ToList();
        Assert.Equal(
            [new DeclarationKey.NamedType("X`0.extension`0( string)"), new DeclarationKey.NamedType("X`0.extension`1( System . Collections . Generic . List < T >)")],
            blocks.Select(b => b.Key));
        Assert.All(blocks, b => Assert.Equal(["X"], b.Names));
        // A member is keyed under its block, so members of the same name in two blocks do not collide.
        var member = Assert.IsType<DeclarationKey.Member>(declarations.All.Single(d => d.Names[0] == "Twice").Key);
        Assert.Equal(blocks[1].Key, member.Container);
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
