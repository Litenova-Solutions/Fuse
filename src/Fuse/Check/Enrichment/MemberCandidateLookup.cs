using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
namespace Fuse.Check.Enrichment;

/// <summary>
///     Finds the candidate members and overloads a repair packet names: the accessible members with the
///     closest names to a missing member, and the reachable overloads of a call with the wrong arguments.
/// </summary>
internal static class MemberCandidateLookup
{
    /// <summary>
    ///     Finds the accessible members with the closest names to the missing member in
    ///     <paramref name="diagnostic"/>, formatted as minimal signatures, closest edit distance first.
    /// </summary>
    internal static string[] MissingMemberCandidates(
        Diagnostic diagnostic,
        SemanticModel model,
        Compilation compilation,
        ISymbol within,
        SyntaxToken token,
        TextSpan span,
        CancellationToken cancellationToken)
    {
        var (receiverType, staticType, missingName) = ResolveMemberReceiver(diagnostic, model, token, span, cancellationToken);
        if (string.IsNullOrEmpty(missingName))
            return [];

        var isStaticLookup = diagnostic.Id == "CS0117" && staticType is not null;
        var targetType = isStaticLookup ? staticType : receiverType;
        if (targetType is null or IErrorTypeSymbol)
            return [];

        IEnumerable<ISymbol> rawMembers;
        if (isStaticLookup)
        {
            rawMembers = model.LookupStaticMembers(span.Start, targetType, null);
        }
        else
        {
            var lookupType = UnwrapNullable(targetType);
            rawMembers = model.LookupSymbols(span.Start, lookupType, null, includeReducedExtensionMethods: true)
                .Where(m => !m.IsStatic || m is IMethodSymbol { IsExtensionMethod: true });
        }

        var ranked = new List<(int CiDist, int CsDist, string Signature)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in rawMembers)
        {
            if (!IsSuggestibleMember(member, compilation, within))
                continue;

            var formatted = FormatCandidateMember(member, model, span.Start);
            if (!seen.Add(formatted))
                continue;

            var ciDist = LevenshteinDistance(missingName, member.Name, StringComparison.OrdinalIgnoreCase);
            var csDist = LevenshteinDistance(missingName, member.Name, StringComparison.Ordinal);
            ranked.Add((ciDist, csDist, formatted));
        }

        return ranked
            .OrderBy(r => r.CiDist)
            .ThenBy(r => r.CsDist)
            .ThenBy(r => r.Signature, StringComparer.Ordinal)
            .Take(RepairPackets.MaxSuggestions)
            .Select(r => r.Signature)
            .ToArray();
    }

    private static (ITypeSymbol? ReceiverType, ITypeSymbol? StaticType, string? MissingName) ResolveMemberReceiver(
        Diagnostic diagnostic,
        SemanticModel model,
        SyntaxToken token,
        TextSpan span,
        CancellationToken cancellationToken)
    {
        for (SyntaxNode? node = token.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case MemberAccessExpressionSyntax access when access.Span.IntersectsWith(span):
                    var exprSymbol = model.GetSymbolInfo(access.Expression, cancellationToken).Symbol;
                    var exprType = model.GetTypeInfo(access.Expression, cancellationToken).Type;
                    return (exprType, exprSymbol as ITypeSymbol ?? exprType, access.Name.Identifier.ValueText);

                case MemberBindingExpressionSyntax binding when binding.Span.IntersectsWith(span):
                    var conditional = binding.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault();
                    var condType = conditional is null ? null : model.GetTypeInfo(conditional.Expression, cancellationToken).Type;
                    return (condType, null, binding.Name.Identifier.ValueText);

                case AssignmentExpressionSyntax assign when assign.Parent is InitializerExpressionSyntax init
                    && init.Parent is BaseObjectCreationExpressionSyntax create
                    && assign.Left is IdentifierNameSyntax leftName:
                    return (model.GetTypeInfo(create, cancellationToken).Type, null, leftName.Identifier.ValueText);

                case StatementSyntax or MemberDeclarationSyntax:
                    return (null, null, RepairPackets.SecondQuotedTerm(diagnostic.GetMessage(CultureInfo.InvariantCulture)));
            }
        }

        return (null, null, RepairPackets.SecondQuotedTerm(diagnostic.GetMessage(CultureInfo.InvariantCulture)));
    }

    private static bool IsSuggestibleMember(ISymbol member, Compilation compilation, ISymbol within)
    {
        if (member.IsImplicitlyDeclared)
            return false;
        if (member is IFieldSymbol { AssociatedSymbol: not null })
            return false;
        if (member is IMethodSymbol method && method.MethodKind is not (MethodKind.Ordinary or MethodKind.ReducedExtension))
            return false;
        return compilation.IsSymbolAccessibleWithin(member, within);
    }

    private static string FormatCandidateMember(ISymbol member, SemanticModel model, int position)
    {
        var display = member.ToMinimalDisplayString(model, position, RepairPackets.SignatureFormat);
        if (member is IMethodSymbol { ReducedFrom: not null })
            return display + " (extension)";
        return display;
    }

    /// <summary>
    ///     Finds the reachable overloads of the call at <paramref name="token"/>, the bound overload
    ///     first, then nearest arity, formatted as minimal signatures.
    /// </summary>
    internal static string[] OverloadCandidates(
        SemanticModel model,
        Compilation compilation,
        ISymbol within,
        SyntaxToken token,
        CancellationToken cancellationToken)
    {
        SyntaxNode? callNode = null;
        for (SyntaxNode? node = token.Parent; node is not null; node = node.Parent)
        {
            if (node is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax or AttributeSyntax or ConstructorInitializerSyntax or BaseTypeSyntax)
            {
                callNode = node;
                break;
            }

            if (node is BaseTypeDeclarationSyntax)
            {
                callNode = node;
                break;
            }
        }

        if (callNode is null)
            return [];

        var actualArgCount = ArgumentCountOf(callNode);
        var collected = new List<IMethodSymbol>();
        var seenSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        void AddMethod(ISymbol? symbol)
        {
            if (symbol is IMethodSymbol method && seenSymbols.Add(method) && compilation.IsSymbolAccessibleWithin(method, within))
                collected.Add(method);
        }

        var symbolInfo = model.GetSymbolInfo(callNode, cancellationToken);
        AddMethod(symbolInfo.Symbol);
        foreach (var candidate in symbolInfo.CandidateSymbols)
            AddMethod(candidate);

        switch (callNode)
        {
            case InvocationExpressionSyntax invocation:
                if (invocation.Expression is MemberAccessExpressionSyntax access)
                {
                    var container = model.GetSymbolInfo(access.Expression, cancellationToken).Symbol as ITypeSymbol
                        ?? model.GetTypeInfo(access.Expression, cancellationToken).Type;
                    if (container is not null)
                    {
                        container = UnwrapNullable(container);
                        foreach (var sym in model.LookupSymbols(invocation.SpanStart, container, access.Name.Identifier.ValueText, includeReducedExtensionMethods: true))
                            AddMethod(sym);
                    }
                }
                else if (invocation.Expression is SimpleNameSyntax simple)
                {
                    foreach (var sym in model.LookupSymbols(invocation.SpanStart, null, simple.Identifier.ValueText, includeReducedExtensionMethods: false))
                        AddMethod(sym);
                }

                if (model.GetTypeInfo(invocation.Expression, cancellationToken).Type is INamedTypeSymbol { DelegateInvokeMethod: { } invoke })
                    AddMethod(invoke);
                break;

            case BaseObjectCreationExpressionSyntax create:
                if (model.GetTypeInfo(create, cancellationToken).Type is INamedTypeSymbol createdType)
                {
                    foreach (var ctor in createdType.InstanceConstructors)
                        AddMethod(ctor);
                }

                break;

            case AttributeSyntax attribute:
                if (model.GetTypeInfo(attribute, cancellationToken).Type is INamedTypeSymbol attrType)
                {
                    foreach (var ctor in attrType.InstanceConstructors)
                        AddMethod(ctor);
                }

                break;

            case ConstructorInitializerSyntax init:
                if (init.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault() is { } enclosing
                    && model.GetDeclaredSymbol(enclosing, cancellationToken) is INamedTypeSymbol enclosingType)
                {
                    var target = init.ThisOrBaseKeyword.IsKind(SyntaxKind.BaseKeyword) ? enclosingType.BaseType : enclosingType;
                    if (target is not null)
                    {
                        foreach (var ctor in target.InstanceConstructors)
                            AddMethod(ctor);
                    }
                }

                break;

            case BaseTypeSyntax or BaseTypeDeclarationSyntax:
                var decl = callNode as BaseTypeDeclarationSyntax ?? callNode.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
                if (decl is not null && model.GetDeclaredSymbol(decl, cancellationToken) is INamedTypeSymbol derivedType && derivedType.BaseType is { } baseType)
                {
                    foreach (var ctor in baseType.InstanceConstructors)
                        AddMethod(ctor);
                }

                break;
        }

        var ranked = new List<(int PrimaryRank, int ArgDelta, int ParamCount, string Signature)>();
        var seenText = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in collected)
        {
            var sig = FormatOverloadSignature(method, model, callNode.SpanStart);
            if (!seenText.Add(sig))
                continue;
            var isBoundPrimary = SymbolEqualityComparer.Default.Equals(method, symbolInfo.Symbol) ? 0 : 1;
            var delta = actualArgCount < 0 ? 0 : Math.Abs(method.Parameters.Length - actualArgCount);
            ranked.Add((isBoundPrimary, delta, method.Parameters.Length, sig));
        }

        return ranked
            .OrderBy(r => r.PrimaryRank)
            .ThenBy(r => r.ArgDelta)
            .ThenBy(r => r.ParamCount)
            .ThenBy(r => r.Signature, StringComparer.Ordinal)
            .Take(RepairPackets.MaxSuggestions)
            .Select(r => r.Signature)
            .ToArray();
    }

    private static int ArgumentCountOf(SyntaxNode callNode) =>
        callNode switch
        {
            InvocationExpressionSyntax i => i.ArgumentList.Arguments.Count,
            BaseObjectCreationExpressionSyntax c => c.ArgumentList?.Arguments.Count ?? 0,
            AttributeSyntax a => a.ArgumentList?.Arguments.Count ?? 0,
            ConstructorInitializerSyntax ci => ci.ArgumentList.Arguments.Count,
            _ => -1,
        };

    private static string FormatOverloadSignature(IMethodSymbol method, SemanticModel model, int position)
    {
        if (method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor)
        {
            var parameters = string.Join(", ", method.Parameters.Select(p => p.ToMinimalDisplayString(model, position, RepairPackets.SignatureFormat)));
            return $"{method.ContainingType.ToMinimalDisplayString(model, position, RepairPackets.SignatureFormat)}({parameters})";
        }

        return FormatCandidateMember(method, model, position);
    }

    private static ITypeSymbol UnwrapNullable(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments.Length: 1 } nullable)
            return nullable.TypeArguments[0];
        return type;
    }

    /// <summary>Computes Levenshtein edit distance between two strings under <paramref name="comparison"/>.</summary>
    private static int LevenshteinDistance(string source, string target, StringComparison comparison)
    {
        if (string.Equals(source, target, comparison))
            return 0;
        if (source.Length == 0)
            return target.Length;
        if (target.Length == 0)
            return source.Length;

        var ignoreCase = comparison is StringComparison.OrdinalIgnoreCase or StringComparison.InvariantCultureIgnoreCase or StringComparison.CurrentCultureIgnoreCase;
        var a = ignoreCase ? source.ToUpperInvariant() : source;
        var b = ignoreCase ? target.ToUpperInvariant() : target;

        var costs = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            costs[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            var prev = costs[0];
            costs[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cur = costs[j];
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                costs[j] = Math.Min(Math.Min(costs[j] + 1, costs[j - 1] + 1), prev + cost);
                prev = cur;
            }
        }

        return costs[b.Length];
    }
}
