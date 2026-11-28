using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
namespace Fuse.Check.Enrichment;

/// <summary>
///     Writes the ready-to-paste stub a repair packet names for an unimplemented interface member:
///     a method that throws, an auto-property, or an event declaration.
/// </summary>
internal static class InterfaceStubGenerator
{
    private static readonly SymbolDisplayFormat StubMemberFormat =
        new(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints,
            memberOptions: SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeParameters,
            parameterOptions: SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeDefaultValue,
            propertyStyle: SymbolDisplayPropertyStyle.NameOnly,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    /// <summary>
    ///     Writes the stub for the unimplemented interface member in <paramref name="diagnostic"/>,
    ///     or null when no member of the implemented interfaces still needs one.
    /// </summary>
    internal static string? UnimplementedInterfaceStub(
        Diagnostic diagnostic,
        SemanticModel model,
        SyntaxToken token,
        CancellationToken cancellationToken)
    {
        var typeDecl = token.Parent?.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        if (typeDecl is null || model.GetDeclaredSymbol(typeDecl, cancellationToken) is not INamedTypeSymbol implType)
            return null;

        var baseTypeNode = token.Parent?.AncestorsAndSelf().OfType<BaseTypeSyntax>().FirstOrDefault();
        var ifaceAtDiagnostic = baseTypeNode is null ? null : model.GetTypeInfo(baseTypeNode.Type, cancellationToken).Type as INamedTypeSymbol;

        var interfaces = ifaceAtDiagnostic is not null
            ? new[] { ifaceAtDiagnostic }.Concat(ifaceAtDiagnostic.AllInterfaces).Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            : implType.AllInterfaces;

        var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
        var quotedMember = RepairPackets.SecondQuotedTerm(message);
        var unimplemented = new List<(int MatchScore, ISymbol Member, INamedTypeSymbol Interface)>();

        foreach (var iface in interfaces)
        {
            if (iface.TypeKind != TypeKind.Interface)
                continue;
            foreach (var member in iface.GetMembers())
            {
                if (!RequiresImplementation(member))
                    continue;
                if (IsMemberImplemented(implType, member))
                    continue;
                var score = InterfaceMatchScore(member, iface, quotedMember);
                unimplemented.Add((score, member, iface));
            }
        }

        if (unimplemented.Count == 0)
            return null;

        var best = unimplemented
            .OrderBy(u => u.MatchScore)
            .ThenBy(u => u.Member.Name, StringComparer.Ordinal)
            .First();

        return FormatInterfaceStub(best.Member, best.Interface, implType, model, typeDecl.SpanStart);
    }

    private static bool RequiresImplementation(ISymbol member)
    {
        if (member.IsStatic && !member.IsAbstract)
            return false;
        return member switch
        {
            IMethodSymbol method => method.MethodKind == MethodKind.Ordinary && (method.IsAbstract || !HasDefaultImplementation(method)),
            IPropertySymbol prop => prop.IsAbstract || !HasDefaultImplementation(prop),
            IEventSymbol evt => evt.IsAbstract,
            _ => false,
        };
    }

    private static bool HasDefaultImplementation(IMethodSymbol method) => !method.IsAbstract;

    private static bool HasDefaultImplementation(IPropertySymbol prop) => !prop.IsAbstract;

    private static bool IsMemberImplemented(INamedTypeSymbol implType, ISymbol member)
    {
        if (implType.FindImplementationForInterfaceMember(member) is null)
            return false;
        if (member is IPropertySymbol prop)
        {
            if (prop.GetMethod is not null && implType.FindImplementationForInterfaceMember(prop.GetMethod) is null)
                return false;
            if (prop.SetMethod is not null && implType.FindImplementationForInterfaceMember(prop.SetMethod) is null)
                return false;
        }

        return true;
    }

    private static int InterfaceMatchScore(ISymbol member, INamedTypeSymbol iface, string? quotedMember)
    {
        if (string.IsNullOrEmpty(quotedMember))
            return 1;
        var clean = quotedMember;
        if (clean.EndsWith(".get", StringComparison.Ordinal) || clean.EndsWith(".set", StringComparison.Ordinal))
            clean = clean[..^4];
        var ifaceQualified = iface.Name + "." + member.Name;
        var fullDisplay = member.ToDisplayString(RepairPackets.SignatureFormat);
        if (clean.Contains(fullDisplay, StringComparison.Ordinal))
            return 0;
        if (clean.StartsWith(ifaceQualified, StringComparison.Ordinal))
            return 1;
        if (clean.Contains("." + member.Name, StringComparison.Ordinal))
            return 2;
        return 10;
    }

    private static string FormatInterfaceStub(
        ISymbol member,
        INamedTypeSymbol iface,
        INamedTypeSymbol implType,
        SemanticModel model,
        int position)
    {
        var needsExplicit = NeedsExplicitImplementation(implType, member);
        var staticKeyword = member.IsStatic ? "static " : "";
        var prefix = needsExplicit ? "" : $"public {staticKeyword}";
        var explicitQualifier = needsExplicit ? iface.ToMinimalDisplayString(model, position, RepairPackets.SignatureFormat) + "." : "";

        switch (member)
        {
            case IMethodSymbol method:
                var returnType = method.ReturnType.ToMinimalDisplayString(model, position, RepairPackets.SignatureFormat);
                var typeParams = method.TypeParameters.IsEmpty
                    ? ""
                    : "<" + string.Join(", ", method.TypeParameters.Select(tp => tp.Name)) + ">";
                var methodParams = string.Join(", ", method.Parameters.Select(p => p.ToMinimalDisplayString(model, position, RepairPackets.SignatureFormat)));
                var fullConstraints = method.ToMinimalDisplayString(model, position, StubMemberFormat);
                var whereIdx = fullConstraints.IndexOf(" where ", StringComparison.Ordinal);
                var constraints = whereIdx >= 0 ? fullConstraints[whereIdx..] : "";
                return $"{prefix}{returnType} {explicitQualifier}{method.Name}{typeParams}({methodParams}){constraints} => throw new NotImplementedException();";

            case IPropertySymbol prop:
                var propType = prop.Type.ToMinimalDisplayString(model, position, RepairPackets.SignatureFormat);
                var accessors = new List<string>();
                if (prop.GetMethod is not null)
                    accessors.Add("get;");
                if (prop.SetMethod is not null)
                    accessors.Add(prop.SetMethod.IsInitOnly ? "init;" : "set;");
                var accessorText = string.Join(" ", accessors);
                if (prop.IsIndexer)
                {
                    var idxParams = string.Join(", ", prop.Parameters.Select(p => p.ToMinimalDisplayString(model, position, RepairPackets.SignatureFormat)));
                    return $"{prefix}{propType} {explicitQualifier}this[{idxParams}] {{ {accessorText} }}";
                }

                return $"{prefix}{propType} {explicitQualifier}{prop.Name} {{ {accessorText} }}";

            case IEventSymbol evt:
                var evtType = evt.Type.ToMinimalDisplayString(model, position, RepairPackets.SignatureFormat);
                return $"{prefix}event {evtType} {explicitQualifier}{evt.Name};";

            default:
                return member.ToMinimalDisplayString(model, position, RepairPackets.SignatureFormat);
        }
    }

    private static bool NeedsExplicitImplementation(INamedTypeSymbol implType, ISymbol interfaceMember)
    {
        if (interfaceMember is IMethodSymbol method)
        {
            foreach (var existing in implType.GetMembers(method.Name).OfType<IMethodSymbol>())
            {
                if (existing.Arity == method.Arity
                    && existing.Parameters.Length == method.Parameters.Length
                    && !SymbolEqualityComparer.Default.Equals(existing.ReturnType, method.ReturnType)
                    && existing.Parameters.Select(p => p.Type).SequenceEqual(method.Parameters.Select(p => p.Type), SymbolEqualityComparer.Default))
                {
                    return true;
                }
            }
        }
        else if (interfaceMember is IPropertySymbol prop && !prop.IsIndexer)
        {
            foreach (var existing in implType.GetMembers(prop.Name).OfType<IPropertySymbol>())
            {
                if (!SymbolEqualityComparer.Default.Equals(existing.Type, prop.Type))
                    return true;
            }
        }

        return false;
    }
}
