namespace Fuse.Changes.Model;

/// <summary>
///     The identity of one declaration within a file: a using directive, a file-level attribute list, top-level
///     statements, a type or a member. It stays the same across edits that keep the declaration's name, its containing
///     type and its parameter types, so the declarations of a file at HEAD and in the working tree are compared key by
///     key.
/// </summary>
/// <remarks>
///     A key is read from syntax alone and is compared ordinally, with formatting and comments left out of every part
///     that holds code. Two parts of a partial type or member in one file share a key, and so do two extension blocks of
///     one class for the same receiver.
/// </remarks>
internal abstract record DeclarationKey
{
    private DeclarationKey()
    {
    }

    /// <summary>An ordinary using directive, including a <c>using static</c> or an alias, inside or outside a namespace.</summary>
    /// <param name="Directive">The directive's tokens separated by single spaces.</param>
    public sealed record Using(string Directive) : DeclarationKey;

    /// <summary>A <c>global using</c> directive, which applies to every file of the project.</summary>
    /// <param name="Directive">The directive's tokens separated by single spaces.</param>
    public sealed record GlobalUsing(string Directive) : DeclarationKey;

    /// <summary>An attribute list at file level, such as <c>[assembly: InternalsVisibleTo("Tests")]</c>.</summary>
    /// <param name="Attributes">The list's tokens separated by single spaces.</param>
    public sealed record AssemblyAttribute(string Attributes) : DeclarationKey;

    /// <summary>The top-level statements of the file, which the compiler turns into the application's entry point.</summary>
    public sealed record TopLevelStatements : DeclarationKey;

    /// <summary>A class, struct, interface, enum, record, delegate or extension block.</summary>
    /// <param name="Name">
    ///     The namespace-qualified name with each type's arity after a backtick, containing types first:
    ///     <c>Shop.Order`0.Line`1</c>. An extension block, which has no name, is <c>extension</c> with its arity and its
    ///     receiver's type: <c>Shop.Text`0.extension`0( string)</c>.
    /// </param>
    public sealed record NamedType(string Name) : DeclarationKey;

    /// <summary>A member of a type: a method, constructor, finalizer, property, indexer, event, field, operator or enum member.</summary>
    /// <param name="Container">The type that declares the member.</param>
    /// <param name="Signature">
    ///     A letter for the kind of member and what tells it apart from the type's other members: the name, the explicit
    ///     interface for an explicit implementation, the arity, and the parameter types with their modifiers
    ///     (<c>M:Add`0( int, int)</c>, <c>P:Name</c>, <c>C:( string)</c>, <c>C:static()</c>). A field or an event field
    ///     with several variables has one key per variable.
    /// </param>
    public sealed record Member(NamedType Container, string Signature) : DeclarationKey;
}
