using System.Text;
using Fuse.Changes.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Changes;

/// <summary>
///     The declarations of one version of a C# file, each under its <see cref="DeclarationKey"/>. Both questions Fuse
///     asks about a changed file compare two of these, one read from HEAD and one from the working tree:
///     <see cref="SurfaceDiff"/> asks whether what other files can see changed, and <see cref="CodeDiff"/> asks whose
///     code changed.
/// </summary>
/// <remarks>
///     <para>
///         Read from syntax alone, so it costs a parse and no binding. When the file declares one key more than once (the
///         parts of a partial type, the definition and the implementation of a partial member, or two extension blocks
///         for the same receiver), they are one declaration with every part in <see cref="DeclarationNode.Parts"/> and
///         every part's surface in its <see cref="DeclarationNode.Surface"/>, so an edit to any part is seen.
///     </para>
///     <para>
///         The order is the file's: global usings and file-level attribute lists, then the namespaces, types and members
///         as they are written, each type before its members, then the ordinary using directives. A declaration with
///         several parts stands where its first part does.
///     </para>
/// </remarks>
internal sealed class FileDeclarations
{
    private readonly List<DeclarationNode> _all = [];
    private readonly Dictionary<DeclarationKey, int> _indexOf = [];

    // Collected by the one walk of the file's declarations, because a using directive carries the names of every type the
    // file declares and so is added after the walk. A second walk of the whole tree for them cost as much as the rest.
    private readonly List<string> _typeNames = [];
    private readonly List<UsingDirectiveSyntax> _usings = [];

    private FileDeclarations()
    {
    }

    /// <summary>The declarations of a file that does not exist, at HEAD for an added file or on disk for a deleted one.</summary>
    public static FileDeclarations Empty { get; } = new();

    /// <summary>Every declaration in the file's order.</summary>
    public IReadOnlyList<DeclarationNode> All => _all;

    /// <summary>The declaration under <paramref name="key"/>, or null when this version of the file does not declare it.</summary>
    public DeclarationNode? Find(DeclarationKey key) => _indexOf.TryGetValue(key, out var index) ? _all[index] : null;

    /// <summary>Reads the declarations of the file whose syntax root is <paramref name="root"/>.</summary>
    public static FileDeclarations Of(SyntaxNode root)
    {
        var declarations = new FileDeclarations();
        if (root is CompilationUnitSyntax unit)
        {
            foreach (var global in unit.Usings.Where(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)))
                declarations.Add(new DeclarationKey.GlobalUsing(Flat(global)), global, Flat(global), [], null);
            foreach (var list in unit.AttributeLists)
                declarations.Add(new DeclarationKey.AssemblyAttribute(Flat(list)), list, Flat(list), [], null);
        }

        // Top-level statements have no surface: other files cannot call the entry point they make.
        if (root.ChildNodes().OfType<GlobalStatementSyntax>().Any())
            declarations.Add(new DeclarationKey.TopLevelStatements(), root, null, [], null);

        declarations.Walk(root, "");

        // An ordinary using changes what the type names in this file's declarations resolve to, so removing one can change
        // every signature here without changing their text. Each carries the file's own type names, which is how the files
        // that use those declarations are found.
        string[] types = [.. declarations._typeNames.Distinct(StringComparer.Ordinal)];
        foreach (var directive in declarations._usings)
            declarations.Add(new DeclarationKey.Using(Flat(directive)), directive, Flat(directive), types, null);
        return declarations;
    }

    /// <summary>
    ///     Adds a declaration, or a further part of one the file already declared under <paramref name="key"/>. A further
    ///     part keeps the first part's names and container, which are the same for every part.
    /// </summary>
    private void Add(DeclarationKey key, SyntaxNode node, string? surface, IReadOnlyList<string> names, DeclarationKey.NamedType? container)
    {
        if (_indexOf.TryGetValue(key, out var index))
        {
            var first = _all[index];
            _all[index] = first with
            {
                Parts = [.. first.Parts, node],
                Surface = first.Surface is null ? surface : surface is null ? first.Surface : first.Surface + "\n" + surface,
            };
            return;
        }

        _indexOf[key] = _all.Count;
        _all.Add(new DeclarationNode(key, [node], surface, names, container));
    }

    /// <summary>
    ///     Adds the namespaces, types and delegates under <paramref name="node"/>, the compilation unit or a namespace, and
    ///     keeps its ordinary using directives for <see cref="Of"/> to add last.
    /// </summary>
    private void Walk(SyntaxNode node, string container)
    {
        foreach (var child in node.ChildNodes())
        {
            switch (child)
            {
                case UsingDirectiveSyntax directive when !directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword):
                    _usings.Add(directive);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    Walk(ns, Qualify(container, Dotted(ns.Name)));
                    break;
                case DelegateDeclarationSyntax del:
                    _typeNames.Add(del.Identifier.Text);
                    Add(new DeclarationKey.NamedType($"{Qualify(container, del.Identifier.Text)}`{Arity(del.TypeParameterList)}"), del, Flat(del), [del.Identifier.Text], null);
                    break;
                case BaseTypeDeclarationSyntax type:
                    AddType(type, container, null);
                    break;
            }
        }
    }

    private void AddType(BaseTypeDeclarationSyntax type, string container, DeclarationKey.NamedType? outer)
    {
        var typeParams = type is TypeDeclarationSyntax t ? t.TypeParameterList : null;
        string name;
        DeclarationKey.NamedType key;
        if (type is ExtensionBlockDeclarationSyntax block)
        {
            // An extension block has no name. Its receiver tells it apart from the other blocks of its class, and code
            // that names anything of it names the class.
            name = (type.Parent as BaseTypeDeclarationSyntax)?.Identifier.Text ?? "";
            key = new DeclarationKey.NamedType($"{Qualify(container, "extension")}`{Arity(typeParams)}({ParameterTypes(block.ParameterList)})");
        }
        else
        {
            name = type.Identifier.Text;
            key = new DeclarationKey.NamedType($"{Qualify(container, name)}`{Arity(typeParams)}");
            _typeNames.Add(name);
        }

        var header = new StringBuilder();
        header.Append(Flat(type.AttributeLists)).Append(' ').Append(Flat(type.Modifiers)).Append(' ');
        header.Append(type switch
        {
            RecordDeclarationSyntax r => r.Keyword.Text + r.ClassOrStructKeyword.Text,
            TypeDeclarationSyntax td => td.Keyword.Text,
            EnumDeclarationSyntax e => e.EnumKeyword.Text,
            _ => "",
        });
        if (type is not ExtensionBlockDeclarationSyntax)
            header.Append(' ').Append(name);
        header.Append(Flat(typeParams));
        if (type is TypeDeclarationSyntax withParams)
            header.Append(Flat(withParams.ParameterList)).Append(Flat(withParams.ConstraintClauses));
        header.Append(Flat(type.BaseList));
        Add(key, type, header.ToString(), [name], outer);

        if (type is EnumDeclarationSyntax enumDeclaration)
        {
            foreach (var member in enumDeclaration.Members)
                Add(new DeclarationKey.Member(key, "F:" + member.Identifier.Text), member, Flat(member), [member.Identifier.Text, name], key);
            return;
        }

        if (type is not TypeDeclarationSyntax typeDeclaration)
            return;
        foreach (var member in typeDeclaration.Members)
            AddMember(member, key, name);
    }

    private void AddMember(MemberDeclarationSyntax member, DeclarationKey.NamedType type, string typeName)
    {
        var prefix = Flat(member.AttributeLists) + " " + Flat(member.Modifiers);
        switch (member)
        {
            case BaseTypeDeclarationSyntax nested:
                AddType(nested, type.Name, type);
                break;
            case DelegateDeclarationSyntax del:
                _typeNames.Add(del.Identifier.Text);
                Add(new DeclarationKey.NamedType($"{type.Name}.{del.Identifier.Text}`{Arity(del.TypeParameterList)}"), del, Flat(del), [del.Identifier.Text, typeName], type);
                break;
            case MethodDeclarationSyntax method:
                Add(
                    new DeclarationKey.Member(type, $"M:{Flat(method.ExplicitInterfaceSpecifier)}{method.Identifier.Text}`{Arity(method.TypeParameterList)}({ParameterTypes(method.ParameterList)})"),
                    method,
                    $"{prefix} {Flat(method.ReturnType)} {method.Identifier.Text}{Flat(method.TypeParameterList)}{Flat(method.ParameterList)}{Flat(method.ConstraintClauses)}",
                    [method.Identifier.Text, typeName],
                    type);
                break;
            case ConstructorDeclarationSyntax ctor:
                // A static constructor is not an overload of the parameterless one, so it has a key of its own.
                var staticMarker = ctor.Modifiers.Any(SyntaxKind.StaticKeyword) ? "static" : "";
                Add(new DeclarationKey.Member(type, $"C:{staticMarker}({ParameterTypes(ctor.ParameterList)})"), ctor, $"{prefix} {Flat(ctor.ParameterList)}", [typeName], type);
                break;
            case DestructorDeclarationSyntax destructor:
                // Only the runtime calls a finalizer, so other files see nothing of it.
                Add(new DeclarationKey.Member(type, "D:"), destructor, null, [typeName], type);
                break;
            case PropertyDeclarationSyntax property:
                Add(
                    new DeclarationKey.Member(type, $"P:{Flat(property.ExplicitInterfaceSpecifier)}{property.Identifier.Text}"),
                    property,
                    $"{prefix} {Flat(property.Type)} {property.Identifier.Text} {Accessors(property.AccessorList, property.ExpressionBody)}",
                    [property.Identifier.Text, typeName],
                    type);
                break;
            case IndexerDeclarationSyntax indexer:
                Add(
                    new DeclarationKey.Member(type, $"I:{Flat(indexer.ExplicitInterfaceSpecifier)}[{ParameterTypes(indexer.ParameterList)}]"),
                    indexer,
                    $"{prefix} {Flat(indexer.Type)} {Flat(indexer.ParameterList)} {Accessors(indexer.AccessorList, indexer.ExpressionBody)}",
                    [typeName],
                    type);
                break;
            case EventDeclarationSyntax evt:
                Add(new DeclarationKey.Member(type, $"E:{Flat(evt.ExplicitInterfaceSpecifier)}{evt.Identifier.Text}"), evt, $"{prefix} {Flat(evt.Type)}", [evt.Identifier.Text, typeName], type);
                break;
            case EventFieldDeclarationSyntax eventField:
                foreach (var variable in eventField.Declaration.Variables)
                    Add(new DeclarationKey.Member(type, $"E:{variable.Identifier.Text}"), variable, $"{prefix} {Flat(eventField.Declaration.Type)}", [variable.Identifier.Text, typeName], type);
                break;
            case FieldDeclarationSyntax field:
                var isConst = field.Modifiers.Any(SyntaxKind.ConstKeyword);
                foreach (var variable in field.Declaration.Variables)
                {
                    // A constant's value is part of its surface: it is inlined into every user.
                    var value = isConst ? Flat(variable.Initializer) : "";
                    Add(new DeclarationKey.Member(type, $"F:{variable.Identifier.Text}"), variable, $"{prefix} {Flat(field.Declaration.Type)} {value}", [variable.Identifier.Text, typeName], type);
                }

                break;
            case OperatorDeclarationSyntax op:
                Add(new DeclarationKey.Member(type, $"O:{Flat(op.ExplicitInterfaceSpecifier)}{CheckedMarker(op.CheckedKeyword)}{op.OperatorToken.Text}({ParameterTypes(op.ParameterList)})"), op, $"{prefix} {Flat(op.ReturnType)} {Flat(op.ParameterList)}", [typeName], type);
                break;
            case ConversionOperatorDeclarationSyntax conversion:
                Add(new DeclarationKey.Member(type, $"O:{Flat(conversion.ExplicitInterfaceSpecifier)}{conversion.ImplicitOrExplicitKeyword.Text} {CheckedMarker(conversion.CheckedKeyword)}{Flat(conversion.Type)}({ParameterTypes(conversion.ParameterList)})"), conversion, $"{prefix} {Flat(conversion.ParameterList)}", [typeName], type);
                break;
        }
    }

    private static string Accessors(AccessorListSyntax? accessors, ArrowExpressionClauseSyntax? expressionBody)
    {
        if (accessors is null)
            return expressionBody is null ? "" : "get";
        return string.Join(' ', accessors.Accessors.Select(a => $"{Flat(a.AttributeLists)} {Flat(a.Modifiers)} {a.Keyword.Text}"));
    }

    private static string ParameterTypes(BaseParameterListSyntax? list) =>
        list is null ? "" : string.Join(",", list.Parameters.Select(p => $"{Flat(p.Modifiers)} {Flat(p.Type)}"));

    /// <summary>A checked operator and its unchecked form are two members, so the key tells them apart.</summary>
    private static string CheckedMarker(SyntaxToken checkedKeyword) => checkedKeyword.IsKind(SyntaxKind.CheckedKeyword) ? "checked " : "";

    private static int Arity(TypeParameterListSyntax? list) => list?.Parameters.Count ?? 0;

    private static string Qualify(string container, string name) => container.Length == 0 ? name : container + "." + name;

    /// <summary>Text of a node with all trivia collapsed, so formatting and comments never register as changes.</summary>
    private static string Flat(SyntaxNode? node)
    {
        if (node is null)
            return "";
        var builder = new StringBuilder();
        foreach (var token in node.DescendantTokens())
        {
            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append(token.Text);
        }

        return builder.ToString();
    }

    private static string Flat<T>(SyntaxList<T> list)
        where T : SyntaxNode => string.Join(" ", list.Select(n => Flat(n)));

    /// <summary>Modifiers separated by single spaces, where <see cref="SyntaxTokenList.ToString"/> keeps the whitespace between them.</summary>
    private static string Flat(SyntaxTokenList tokens) => string.Join(" ", tokens.Select(t => t.Text));

    /// <summary>A namespace name with its trivia removed, <c>A.B</c> however it is spaced.</summary>
    private static string Dotted(NameSyntax name) => string.Concat(name.DescendantTokens().Select(t => t.Text));
}
