using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Testing;

/// <summary>
///     Decides whether a changed declaration compiles to the same code as HEAD's, so test selection seeds nothing for
///     it. A declaration qualifies when its header reads the same without bodies and its bodies read the same after
///     every local, parameter and range variable name is replaced with the order it is first seen in and every local
///     type with the type it names, so a local rename, <c>var</c> against the explicit type and an expression body
///     against a block body qualify. Anything else, and anything undecidable, counts as changed.
/// </summary>
internal static class CompiledCode
{
    /// <summary>
    ///     True when <paramref name="changed"/>, a declaration <see cref="Fuse.Changes.CodeDiff"/> found in
    ///     <paramref name="document"/>, compiles to the same code as the same declaration at HEAD. Only a method,
    ///     constructor, operator or conversion, and a property or indexer without an initializer, can qualify; a field,
    ///     an event, a type and anything without a body still select their tests.
    /// </summary>
    /// <param name="document">The working-tree document the declaration changed in.</param>
    /// <param name="changed">The changed declaration.</param>
    /// <param name="model">The working-tree semantic model.</param>
    /// <param name="baseline">The baseline solution, with every changed file at its HEAD content.</param>
    /// <param name="cancellationToken">Cancels binding.</param>
    public static async Task<bool> SameAsHeadAsync(Document document, SyntaxNode changed, SemanticModel model, Solution baseline, CancellationToken cancellationToken)
    {
        try
        {
            if (UnitOf(changed, model) is not { } unit)
                return false;
            if (baseline.GetDocument(document.Id) is not { } baselineDocument)
                return false;
            var baselineModel = await baselineDocument.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var baselineRoot = await baselineDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (baselineModel is null || baselineRoot is null)
                return false;
            if (!ByDocId(baselineModel, baselineRoot).TryGetValue(unit.DocId, out var candidates))
                return false;
            return candidates.Any(c => c.DocId == unit.DocId && HeaderEqual(unit.Header, c.Header) && BodyEqual(model, unit.Bodies, baselineModel, c.Bodies));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    ///     True when two declarations of one method, property or indexer compile to the same code: they share an
    ///     identity, their headers read the same without bodies, and their bodies read the same normalized.
    /// </summary>
    public static bool SameCode(SemanticModel current, SyntaxNode currentDeclaration, SemanticModel baseline, SyntaxNode baselineDeclaration)
    {
        try
        {
            if (UnitOf(currentDeclaration, current) is not { } unit || UnitOf(baselineDeclaration, baseline) is not { } head)
                return false;
            return unit.DocId == head.DocId && HeaderEqual(unit.Header, head.Header) && BodyEqual(current, unit.Bodies, baseline, head.Bodies);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>The comparable declarations of one file version, keyed by the identity both versions share.</summary>
    private static Dictionary<string, List<Comparable>> ByDocId(SemanticModel model, SyntaxNode root)
    {
        var map = new Dictionary<string, List<Comparable>>(StringComparer.Ordinal);
        foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            if (UnitOf(member, model) is not { } unit)
                continue;
            if (!map.TryGetValue(unit.DocId, out var units))
                map[unit.DocId] = units = [];
            units.Add(unit);
        }

        return map;
    }

    /// <summary>
    ///     The comparable form of <paramref name="declaration"/>, or null when it is not a shape this comparison reads:
    ///     a method, constructor, operator or conversion with a body, or a property or indexer without an initializer
    ///     that has at least one body. A signature, constant, attribute or type-header change keeps today's path because
    ///     the header below still differs; a field, an event and a delegate never qualify.
    /// </summary>
    private static Comparable? UnitOf(SyntaxNode declaration, SemanticModel model) =>
        declaration switch
        {
            MethodDeclarationSyntax method when model.GetDeclaredSymbol(method) is IMethodSymbol symbol && symbol.GetDocumentationCommentId() is { } id
                && RawBodyOf(method.Body, method.ExpressionBody, !symbol.ReturnsVoid) is { } body => new Comparable(declaration, [body], id),
            ConstructorDeclarationSyntax ctor when model.GetDeclaredSymbol(ctor) is IMethodSymbol symbol && symbol.GetDocumentationCommentId() is { } id
                && RawBodyOf(ctor.Body, ctor.ExpressionBody, returnsValue: false) is { } body => new Comparable(declaration, [body], id),
            OperatorDeclarationSyntax op when model.GetDeclaredSymbol(op) is IMethodSymbol symbol && symbol.GetDocumentationCommentId() is { } id
                && RawBodyOf(op.Body, op.ExpressionBody, !symbol.ReturnsVoid) is { } body => new Comparable(declaration, [body], id),
            ConversionOperatorDeclarationSyntax conversion when model.GetDeclaredSymbol(conversion) is IMethodSymbol symbol && symbol.GetDocumentationCommentId() is { } id
                && RawBodyOf(conversion.Body, conversion.ExpressionBody, !symbol.ReturnsVoid) is { } body => new Comparable(declaration, [body], id),
            PropertyDeclarationSyntax property when property.Initializer is null && model.GetDeclaredSymbol(property) is IPropertySymbol symbol
                && symbol.GetDocumentationCommentId() is { } id && BodiesOf(property.AccessorList, property.ExpressionBody) is { Count: > 0 } bodies =>
                new Comparable(declaration, bodies, id),
            IndexerDeclarationSyntax indexer when model.GetDeclaredSymbol(indexer) is IPropertySymbol symbol
                && symbol.GetDocumentationCommentId() is { } id && BodiesOf(indexer.AccessorList, indexer.ExpressionBody) is { Count: > 0 } bodies =>
                new Comparable(declaration, bodies, id),
            _ => null,
        };

    /// <summary>One body: a block, an expression body, or null when the member has neither, as an abstract member has.</summary>
    private static RawBody? RawBodyOf(BlockSyntax? block, ArrowExpressionClauseSyntax? arrow, bool returnsValue)
    {
        if (block is not null && arrow is not null)
            return null;
        if (block is not null)
            return new RawBody(block, null, ReturnsValue: true);
        return arrow is null ? null : new RawBody(null, arrow, returnsValue);
    }

    /// <summary>Every accessor body and the expression body of a property or indexer, in source order.</summary>
    private static List<RawBody> BodiesOf(AccessorListSyntax? accessors, ArrowExpressionClauseSyntax? expression)
    {
        var bodies = new List<RawBody>();
        if (accessors is not null)
        {
            foreach (var accessor in accessors.Accessors)
            {
                var returnsValue = !accessor.Keyword.IsKind(SyntaxKind.SetKeyword) && !accessor.Keyword.IsKind(SyntaxKind.InitKeyword);
                if (RawBodyOf(accessor.Body, accessor.ExpressionBody, returnsValue) is { } body)
                    bodies.Add(body);
            }
        }

        if (expression is not null)
            bodies.Add(new RawBody(null, expression, ReturnsValue: true));
        return bodies;
    }

    /// <summary>Whether two headers read the same with every body stripped: signature, constraints, attributes and accessor kinds.</summary>
    private static bool HeaderEqual(SyntaxNode current, SyntaxNode baseline) =>
        SyntaxFactory.AreEquivalent(new HeaderShape().Visit(current), new HeaderShape().Visit(baseline), topLevel: false);

    /// <summary>Whether two body lists read the same normalized, each body against the one in its position.</summary>
    private static bool BodyEqual(SemanticModel current, IReadOnlyList<RawBody> bodies, SemanticModel baseline, IReadOnlyList<RawBody> before)
    {
        if (bodies.Count != before.Count)
            return false;
        var normalized = new Normalizer(current);
        var normalizedBefore = new Normalizer(baseline);
        for (var i = 0; i < bodies.Count; i++)
        {
            var body = normalized.Visit(BlockOf(bodies[i]));
            var head = normalizedBefore.Visit(BlockOf(before[i]));
            if (body is null || head is null || !SyntaxFactory.AreEquivalent(body, head, topLevel: false))
                return false;
        }

        // An identifier HEAD leaves unbound is not a rename this comparison understands.
        return !normalized.Failed && !normalizedBefore.Failed && !normalizedBefore.SawUnbound;
    }

    /// <summary>The body as a block, so an expression body compares against a block body.</summary>
    private static BlockSyntax BlockOf(RawBody body)
    {
        if (body.Block is not null)
            return body.Block;
        var arrow = body.Arrow!;
        var semicolon = SyntaxFactory.Token(SyntaxKind.SemicolonToken);
        StatementSyntax statement = body.ReturnsValue
            ? SyntaxFactory.ReturnStatement(arrow.Expression).WithSemicolonToken(semicolon)
            : SyntaxFactory.ExpressionStatement(arrow.Expression).WithSemicolonToken(semicolon);
        return SyntaxFactory.Block(statement);
    }

    /// <summary>One declaration whose code is compared: its header, its bodies, and the identity the baseline declaration is found under.</summary>
    private sealed record Comparable(SyntaxNode Header, IReadOnlyList<RawBody> Bodies, string DocId);

    /// <summary>One body: a block, or an expression body with whether it returns a value.</summary>
    private sealed record RawBody(BlockSyntax? Block, ArrowExpressionClauseSyntax? Arrow, bool ReturnsValue);

    /// <summary>Strips every body, leaving the header: signature, constraints, attributes and accessor kinds.</summary>
    private sealed class HeaderShape : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
            base.VisitMethodDeclaration(node) is MethodDeclarationSyntax visited
                ? visited.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default)
                : null;

        public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node) =>
            base.VisitConstructorDeclaration(node) is ConstructorDeclarationSyntax visited
                ? visited.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default)
                : null;

        public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node) =>
            base.VisitOperatorDeclaration(node) is OperatorDeclarationSyntax visited
                ? visited.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default)
                : null;

        public override SyntaxNode? VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node) =>
            base.VisitConversionOperatorDeclaration(node) is ConversionOperatorDeclarationSyntax visited
                ? visited.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default)
                : null;

        public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node) =>
            base.VisitPropertyDeclaration(node) is PropertyDeclarationSyntax visited
                ? visited.WithExpressionBody(null).WithSemicolonToken(default)
                : null;

        public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax node) =>
            base.VisitIndexerDeclaration(node) is IndexerDeclarationSyntax visited
                ? visited.WithExpressionBody(null).WithSemicolonToken(default)
                : null;

        public override SyntaxNode? VisitAccessorDeclaration(AccessorDeclarationSyntax node) =>
            base.VisitAccessorDeclaration(node) is AccessorDeclarationSyntax visited
                ? visited.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default)
                : null;
    }

    /// <summary>
    ///     Rewrites a body so a rename does not register: every local, parameter and range variable becomes the order it
    ///     is first seen in, and every local type the type it names. A name under <c>nameof</c> keeps its text, because the
    ///     name is the value there; a name that is an inferred member or a captured argument fails the comparison, for the
    ///     same reason; and a query fails it, because its range variables declare nowhere this rewriter reaches.
    /// </summary>
    private sealed class Normalizer : CSharpSyntaxRewriter
    {
        private readonly SemanticModel _model;
        private readonly Dictionary<ISymbol, string> _names = new(SymbolEqualityComparer.Default);

        public Normalizer(SemanticModel model) => _model = model;

        /// <summary>True when the body holds a construct this normalizer does not rewrite, so the comparison fails.</summary>
        public bool Failed { get; private set; }

        /// <summary>True when an identifier bound to nothing, which on the HEAD side fails the comparison.</summary>
        public bool SawUnbound { get; private set; }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (node.IsVar || IsInNameof(node))
                return node;
            var symbol = _model.GetSymbolInfo(node).Symbol;
            if (symbol is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol)
            {
                // A parameter name is not a value: renaming a local never changes it, so it normalizes as before.
                if (node.Parent is NameColonSyntax or NameEqualsSyntax)
                    return SyntaxFactory.IdentifierName(Placeholder(symbol));
                if (IsInferredMemberName(node) || IsCapturedArgument(node))
                {
                    Failed = true;
                    return node;
                }

                return SyntaxFactory.IdentifierName(Placeholder(symbol));
            }

            if (symbol is null)
                SawUnbound = true;
            return node;
        }

        public override SyntaxNode? VisitVariableDeclaration(VariableDeclarationSyntax node)
        {
            ITypeSymbol? type = null;
            foreach (var variable in node.Variables)
            {
                if (_model.GetDeclaredSymbol(variable) is not ILocalSymbol local || local.Type is IErrorTypeSymbol)
                {
                    Failed = true;
                    return node;
                }

                type ??= local.Type;
            }

            if (type is null || type.TypeKind == TypeKind.Dynamic)
            {
                Failed = true;
                return node;
            }

            return node.WithType(LocalType(node.Type, type)).WithVariables(VisitList(node.Variables));
        }

        public override SyntaxNode? VisitVariableDeclarator(VariableDeclaratorSyntax node)
        {
            if (_model.GetDeclaredSymbol(node) is not ILocalSymbol symbol)
            {
                Failed = true;
                return node;
            }

            var visited = (VariableDeclaratorSyntax?)base.VisitVariableDeclarator(node) ?? node;
            return visited.WithIdentifier(SyntaxFactory.Identifier(Placeholder(symbol)));
        }

        public override SyntaxNode? VisitParameter(ParameterSyntax node)
        {
            if (_model.GetDeclaredSymbol(node) is not IParameterSymbol symbol)
            {
                Failed = true;
                return node;
            }

            var visited = (ParameterSyntax?)base.VisitParameter(node) ?? node;
            return visited.WithIdentifier(SyntaxFactory.Identifier(Placeholder(symbol)));
        }

        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        {
            if (_model.GetDeclaredSymbol(node) is not ILocalSymbol symbol || symbol.Type is IErrorTypeSymbol || symbol.Type.TypeKind == TypeKind.Dynamic)
            {
                Failed = true;
                return node;
            }

            return node.WithType(LocalType(node.Type, symbol.Type))
                .WithIdentifier(SyntaxFactory.Identifier(Placeholder(symbol)))
                .WithExpression(Stay(node.Expression))
                .WithStatement(Stay(node.Statement));
        }

        public override SyntaxNode? VisitCatchDeclaration(CatchDeclarationSyntax node)
        {
            ITypeSymbol? type = null;
            if (node.Type is not null)
            {
                type = (_model.GetDeclaredSymbol(node) as ILocalSymbol)?.Type ?? _model.GetSymbolInfo(node.Type).Symbol as ITypeSymbol;
                if (type is null or IErrorTypeSymbol)
                {
                    Failed = true;
                    return node;
                }
            }

            ILocalSymbol? symbol = null;
            if (!node.Identifier.IsKind(SyntaxKind.None))
            {
                symbol = _model.GetDeclaredSymbol(node) as ILocalSymbol;
                if (symbol is null)
                {
                    Failed = true;
                    return node;
                }
            }

            var visited = (CatchDeclarationSyntax?)base.VisitCatchDeclaration(node) ?? node;
            if (type is not null)
                visited = visited.WithType(LocalType(node.Type!, type));
            return symbol is null ? visited : visited.WithIdentifier(SyntaxFactory.Identifier(Placeholder(symbol)));
        }

        public override SyntaxNode? VisitSingleVariableDesignation(SingleVariableDesignationSyntax node)
        {
            if (_model.GetDeclaredSymbol(node) is not ILocalSymbol symbol)
            {
                Failed = true;
                return node;
            }

            return node.WithIdentifier(SyntaxFactory.Identifier(Placeholder(symbol)));
        }

        public override SyntaxNode? VisitDeclarationExpression(DeclarationExpressionSyntax node)
        {
            if (node.Type is IdentifierNameSyntax { IsVar: true } && node.Designation is not SingleVariableDesignationSyntax)
                return base.VisitDeclarationExpression(node);
            if (DesignatedType(node.Designation, node.Type) is not { } type)
            {
                Failed = true;
                return node;
            }

            var visited = (DeclarationExpressionSyntax?)base.VisitDeclarationExpression(node) ?? node;
            return visited.WithType(LocalType(node.Type, type));
        }

        public override SyntaxNode? VisitDeclarationPattern(DeclarationPatternSyntax node)
        {
            if (node.Type is IdentifierNameSyntax { IsVar: true } && node.Designation is not SingleVariableDesignationSyntax)
                return base.VisitDeclarationPattern(node);
            if (DesignatedType(node.Designation, node.Type) is not { } type)
            {
                Failed = true;
                return node;
            }

            var visited = (DeclarationPatternSyntax?)base.VisitDeclarationPattern(node) ?? node;
            return visited.WithType(LocalType(node.Type, type));
        }

        public override SyntaxNode? VisitTypePattern(TypePatternSyntax node)
        {
            if (_model.GetSymbolInfo(node.Type).Symbol is not ITypeSymbol type || type is IErrorTypeSymbol)
            {
                Failed = true;
                return node;
            }

            var visited = (TypePatternSyntax?)base.VisitTypePattern(node) ?? node;
            return visited.WithType(LocalType(node.Type, type));
        }

        public override SyntaxNode? VisitQueryExpression(QueryExpressionSyntax node)
        {
            Failed = true;
            return node;
        }

        /// <summary>The type a pattern or an <c>out</c> variable declares: its symbol's, or the type the syntax names.</summary>
        private ITypeSymbol? DesignatedType(VariableDesignationSyntax designation, TypeSyntax type)
        {
            if (designation is SingleVariableDesignationSyntax single && _model.GetDeclaredSymbol(single) is ILocalSymbol local && local.Type is not IErrorTypeSymbol)
                return local.Type.TypeKind == TypeKind.Dynamic ? null : local.Type;
            return _model.GetSymbolInfo(type).Symbol is ITypeSymbol named && named is not IErrorTypeSymbol ? named : null;
        }

        /// <summary>The placeholder for <paramref name="symbol"/>, assigned in first-seen order, so a rename keeps it.</summary>
        private string Placeholder(ISymbol symbol)
        {
            if (!_names.TryGetValue(symbol, out var name))
                _names[symbol] = name = "__" + _names.Count;
            return name;
        }

        private T Stay<T>(T node)
            where T : SyntaxNode =>
            (T?)Visit(node) ?? node;

        /// <summary>True when <paramref name="node"/> is the name <c>nameof</c> reads or under it.</summary>
        private static bool IsInNameof(SyntaxNode node)
        {
            for (var current = node.Parent; current is not null; current = current.Parent)
            {
                if (current is InvocationExpressionSyntax invocation && invocation.Expression is IdentifierNameSyntax name && name.Identifier.Text == "nameof")
                    return true;
            }

            return false;
        }

        /// <summary>
        ///     True when <paramref name="node"/> is an inferred member name: an anonymous-object member without an explicit
        ///     name, or a tuple element without one. The name is the value there, so a rename changes what the code does.
        /// </summary>
        private static bool IsInferredMemberName(IdentifierNameSyntax node)
        {
            if (node.Parent is AnonymousObjectMemberDeclaratorSyntax member && member.NameEquals is null && member.Expression == node)
                return true;
            return node.Parent is ArgumentSyntax argument && argument.NameColon is null && argument.Expression == node && argument.Parent is TupleExpressionSyntax;
        }

        /// <summary>True when <paramref name="node"/> sits in an argument whose text a <c>CallerArgumentExpression</c> captures.</summary>
        private bool IsCapturedArgument(SyntaxNode node)
        {
            for (var current = node.Parent; current is not null; current = current.Parent)
            {
                if (current is ArgumentSyntax argument && ArgumentIsCaptured(argument))
                    return true;
                if (current is AttributeArgumentSyntax attribute && AttributeArgumentIsCaptured(attribute))
                    return true;
            }

            return false;
        }

        /// <summary>True when <paramref name="argument"/> binds to a parameter whose text a <c>CallerArgumentExpression</c> captures.</summary>
        private bool ArgumentIsCaptured(ArgumentSyntax argument)
        {
            if (argument.Parent is BracketedArgumentListSyntax bracket && bracket.Parent is ElementAccessExpressionSyntax element)
                return IndexerArgumentIsCaptured(element, bracket, argument);
            if (argument.Parent is not ArgumentListSyntax list)
                return false;
            var method = list.Parent switch
            {
                InvocationExpressionSyntax invocation => _model.GetSymbolInfo(invocation).Symbol as IMethodSymbol
                    ?? _model.GetSymbolInfo(invocation.Expression).Symbol as IMethodSymbol,
                ObjectCreationExpressionSyntax creation => _model.GetSymbolInfo(creation).Symbol as IMethodSymbol,
                ImplicitObjectCreationExpressionSyntax implicitCreation => _model.GetSymbolInfo(implicitCreation).Symbol as IMethodSymbol,
                ConstructorInitializerSyntax initializer => _model.GetSymbolInfo(initializer).Symbol as IMethodSymbol,
                _ => null,
            };
            return method is not null && Captures(method.Parameters, BoundName(list.Arguments, argument, method.Parameters));
        }

        /// <summary>True when the indexer argument <paramref name="argument"/> binds to a captured parameter.</summary>
        private bool IndexerArgumentIsCaptured(ElementAccessExpressionSyntax element, BracketedArgumentListSyntax bracket, ArgumentSyntax argument)
        {
            if (_model.GetSymbolInfo(element).Symbol is not IPropertySymbol indexer)
                return false;
            string? name;
            if (argument.NameColon is not null)
            {
                name = argument.NameColon.Name.Identifier.Text;
            }
            else
            {
                var index = IndexOf(bracket.Arguments, argument);
                name = index >= 0 && index < indexer.Parameters.Length ? indexer.Parameters[index].Name : null;
            }

            return Captures(indexer.Parameters, name);
        }

        /// <summary>True when the attribute argument <paramref name="argument"/> binds to a captured constructor parameter.</summary>
        private bool AttributeArgumentIsCaptured(AttributeArgumentSyntax argument)
        {
            if (argument.Parent is not AttributeArgumentListSyntax list || list.Parent is not AttributeSyntax attribute)
                return false;
            // `Name = value` sets a property, not a constructor parameter.
            if (argument.NameEquals is not null)
                return false;
            if (_model.GetSymbolInfo(attribute).Symbol is not IMethodSymbol constructor)
                return false;
            string? name;
            if (argument.NameColon is not null)
            {
                name = argument.NameColon.Name.Identifier.Text;
            }
            else
            {
                var index = IndexOf(list.Arguments, argument);
                if (index < 0)
                    return false;
                name = index < constructor.Parameters.Length ? constructor.Parameters[index].Name
                    : constructor.Parameters.Length > 0 && constructor.Parameters[^1].IsParams ? constructor.Parameters[^1].Name : null;
            }

            return Captures(constructor.Parameters, name);
        }

        /// <summary>The parameter <paramref name="argument"/> binds to, or null when it binds to none.</summary>
        private static string? BoundName(SeparatedSyntaxList<ArgumentSyntax> arguments, ArgumentSyntax argument, IReadOnlyList<IParameterSymbol> parameters)
        {
            if (argument.NameColon is not null)
                return argument.NameColon.Name.Identifier.Text;
            var index = IndexOf(arguments, argument);
            if (index < 0)
                return null;
            if (index < parameters.Count)
                return parameters[index].Name;
            return parameters.Count > 0 && parameters[^1].IsParams ? parameters[^1].Name : null;
        }

        /// <summary>True when a parameter in <paramref name="parameters"/> captures <paramref name="name"/> with a <c>CallerArgumentExpression</c>.</summary>
        private static bool Captures(IReadOnlyList<IParameterSymbol> parameters, string? name)
        {
            if (name is null)
                return false;
            foreach (var parameter in parameters)
            {
                foreach (var attribute in parameter.GetAttributes())
                {
                    if (attribute.AttributeClass?.Name is not ("CallerArgumentExpressionAttribute" or "CallerArgumentExpression"))
                        continue;
                    // The attribute names the parameter whose text it captures; when it cannot be read, the text could be this argument's.
                    if (attribute.ConstructorArguments.Length == 0 || attribute.ConstructorArguments[0].Value is not string captured)
                        return true;
                    if (captured == name)
                        return true;
                }
            }

            return false;
        }

        /// <summary>The position of <paramref name="argument"/> in <paramref name="arguments"/>, by identity, or -1.</summary>
        private static int IndexOf(SeparatedSyntaxList<ArgumentSyntax> arguments, ArgumentSyntax argument)
        {
            for (var i = 0; i < arguments.Count; i++)
            {
                if (ReferenceEquals(arguments[i], argument))
                    return i;
            }

            return -1;
        }

        /// <summary>The position of <paramref name="argument"/> in <paramref name="arguments"/>, by identity, or -1.</summary>
        private static int IndexOf(SeparatedSyntaxList<AttributeArgumentSyntax> arguments, AttributeArgumentSyntax argument)
        {
            for (var i = 0; i < arguments.Count; i++)
            {
                if (ReferenceEquals(arguments[i], argument))
                    return i;
            }

            return -1;
        }

        /// <summary>The type as the identity it names, keeping <c>ref</c>, so <c>var</c> compares against the explicit type.</summary>
        private static TypeSyntax LocalType(TypeSyntax type, ITypeSymbol symbol)
        {
            var placeholder = (TypeSyntax)SyntaxFactory.IdentifierName("__T<" + symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ">");
            return type is RefTypeSyntax re ? re.WithType(placeholder) : placeholder;
        }
    }
}
