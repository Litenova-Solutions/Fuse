using System.Globalization;
using Fuse.Check.Model;
using Fuse.Paths;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Fuse.Check;

/// <summary>
///     Builds compact, Roslyn-free <see cref="RepairPacket"/> hints from bound compiler diagnostics so agents can
///     fix common errors without reading other files.
/// </summary>
internal static class RepairPacketBuilder
{
    private const int MaxSuggestions = 5;

    private static readonly SymbolDisplayFormat SignatureFormat =
        SymbolDisplayFormat.MinimallyQualifiedFormat
            .WithParameterOptions(
                SymbolDisplayParameterOptions.IncludeExtensionThis
                | SymbolDisplayParameterOptions.IncludeParamsRefOut
                | SymbolDisplayParameterOptions.IncludeType
                | SymbolDisplayParameterOptions.IncludeName
                | SymbolDisplayParameterOptions.IncludeDefaultValue)
            .WithGenericsOptions(
                SymbolDisplayGenericsOptions.IncludeTypeParameters
                | SymbolDisplayGenericsOptions.IncludeTypeConstraints)
            .WithMiscellaneousOptions(
                SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
                | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    private static readonly SymbolDisplayFormat StubMemberFormat =
        new(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints,
            memberOptions: SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeParameters,
            parameterOptions: SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeDefaultValue,
            propertyStyle: SymbolDisplayPropertyStyle.NameOnly,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    /// <summary>
    ///     Builds a deterministic <see cref="RepairPacket"/> for <paramref name="diagnostic"/> using
    ///     <paramref name="model"/>, <paramref name="compilation"/>, and <paramref name="text"/>.
    /// </summary>
    public static RepairPacket Build(
        Diagnostic diagnostic,
        SemanticModel model,
        Compilation compilation,
        SourceText text,
        Solution? solution = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(text);

        var lineSpan = diagnostic.Location.GetLineSpan();
        var errorLine = Math.Clamp(lineSpan.StartLinePosition.Line, 0, Math.Max(0, text.Lines.Count - 1));
        var sourceWindow = BuildSourceWindow(text, errorLine);

        var root = model.SyntaxTree.GetRoot(cancellationToken);
        var span = diagnostic.Location.SourceSpan;
        var token = FindTokenAt(root, span.Start);
        var within = EnclosingWithin(model, token, compilation, cancellationToken);

        IReadOnlyList<string> candidates = [];
        IReadOnlyList<string> suggestedUsings = [];
        string? interfaceStub = null;

        switch (diagnostic.Id)
        {
            case "CS1061":
            case "CS0117":
                candidates = MissingMemberCandidates(diagnostic, model, compilation, within, token, span, cancellationToken);
                break;
            case "CS1503":
            case "CS7036":
            case "CS1501":
            case "CS1729":
                candidates = OverloadCandidates(model, compilation, within, token, cancellationToken);
                break;
            case "CS0246":
                suggestedUsings = MissingTypeUsings(diagnostic, model, compilation, within, token, solution, cancellationToken);
                break;
            case "CS0535":
                interfaceStub = UnimplementedInterfaceStub(diagnostic, model, token, cancellationToken);
                break;
        }

        return new RepairPacket(sourceWindow, candidates, suggestedUsings, interfaceStub);
    }

    /// <summary>
    ///     Attaches <see cref="RepairPacket"/> hints to each error in <paramref name="errors"/> from
    ///     <paramref name="solution"/>, grouping lookups by file.
    /// </summary>
    public static async Task<IReadOnlyList<IntroducedError>> AttachAsync(
        Solution solution,
        RepoRoot root,
        IReadOnlyList<IntroducedError> errors,
        CancellationToken cancellationToken)
    {
        if (errors.Count == 0)
            return errors;

        var fileCache = new Dictionary<string, DocumentSnapshot?>(StringComparer.Ordinal);
        var result = new List<IntroducedError>(errors.Count);

        foreach (var entry in errors)
        {
            var error = entry.Error;
            if (!fileCache.TryGetValue(error.Path, out var snapshot))
            {
                snapshot = await LoadSnapshotAsync(solution, root, error.Path, cancellationToken).ConfigureAwait(false);
                fileCache[error.Path] = snapshot;
            }

            if (snapshot is null)
            {
                result.Add(entry);
                continue;
            }

            var packet = BuildForError(error, snapshot, solution, cancellationToken);
            result.Add(entry with { Repair = packet, Error = error with { Repair = packet } });
        }

        return result;
    }

    private static async Task<DocumentSnapshot?> LoadSnapshotAsync(
        Solution solution,
        RepoRoot root,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var absolute = root.PathOf(relativePath).Absolute;
        var ids = solution.GetDocumentIdsWithFilePath(absolute);
        if (ids.IsEmpty)
            return null;

        Document? document = null;
        foreach (var id in ids)
        {
            if (solution.GetDocument(id) is { } found)
            {
                document = found;
                break;
            }
        }

        if (document is null)
            return null;

        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (model is null)
            return null;

        return new DocumentSnapshot(model, model.Compilation, text);
    }

    private static RepairPacket BuildForError(
        CompilerError error,
        DocumentSnapshot snapshot,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var lineIndex = Math.Clamp(error.Line - 1, 0, Math.Max(0, snapshot.Text.Lines.Count - 1));
        if (!error.FromAnalyzer && error.Line >= 1 && error.Line <= snapshot.Text.Lines.Count)
        {
            var lineSpan = snapshot.Text.Lines[lineIndex].Span;
            var diagnostics = snapshot.Model.GetDiagnostics(lineSpan, cancellationToken);
            var matched = diagnostics.FirstOrDefault(d => Matches(d, error))
                ?? diagnostics.FirstOrDefault(d => d.Id == error.Id && d.Location.GetMappedLineSpan().StartLinePosition.Line + 1 == error.Line);
            if (matched is not null)
                return Build(matched, snapshot.Model, snapshot.Compilation, snapshot.Text, solution, cancellationToken);
        }

        return new RepairPacket(BuildSourceWindow(snapshot.Text, lineIndex), [], [], null);
    }

    private static bool Matches(Diagnostic diagnostic, CompilerError error)
    {
        if (!string.Equals(diagnostic.Id, error.Id, StringComparison.Ordinal))
            return false;
        var span = diagnostic.Location.GetMappedLineSpan();
        return span.StartLinePosition.Line + 1 == error.Line
            && span.StartLinePosition.Character + 1 == error.Column;
    }

    private static List<string> BuildSourceWindow(SourceText text, int errorLineIndex)
    {
        if (text.Lines.Count == 0)
            return [];
        var start = Math.Max(0, errorLineIndex - 2);
        var end = Math.Min(text.Lines.Count - 1, errorLineIndex + 2);
        var window = new List<string>(end - start + 1);
        for (var i = start; i <= end; i++)
        {
            var prefix = i == errorLineIndex ? "> " : "  ";
            var content = text.Lines[i].ToString().TrimEnd('\r');
            window.Add($"{prefix}{i + 1}: {content}");
        }

        return window;
    }

    private static SyntaxToken FindTokenAt(SyntaxNode root, int position)
    {
        var clamped = Math.Clamp(position, root.FullSpan.Start, Math.Max(root.FullSpan.Start, root.FullSpan.End - 1));
        return root.FindToken(clamped);
    }

    private static ISymbol EnclosingWithin(SemanticModel model, SyntaxToken token, Compilation compilation, CancellationToken cancellationToken)
    {
        var typeSyntax = token.Parent?.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        if (typeSyntax is not null && model.GetDeclaredSymbol(typeSyntax, cancellationToken) is { } typeSymbol)
            return typeSymbol;
        return compilation.Assembly;
    }

    private static string[] MissingMemberCandidates(
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
            .Take(MaxSuggestions)
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
                    return (null, null, SecondQuotedTerm(diagnostic.GetMessage(CultureInfo.InvariantCulture)));
            }
        }

        return (null, null, SecondQuotedTerm(diagnostic.GetMessage(CultureInfo.InvariantCulture)));
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
        var display = member.ToMinimalDisplayString(model, position, SignatureFormat);
        if (member is IMethodSymbol { ReducedFrom: not null })
            return display + " (extension)";
        return display;
    }

    private static string[] OverloadCandidates(
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
            .Take(MaxSuggestions)
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
            var parameters = string.Join(", ", method.Parameters.Select(p => p.ToMinimalDisplayString(model, position, SignatureFormat)));
            return $"{method.ContainingType.ToMinimalDisplayString(model, position, SignatureFormat)}({parameters})";
        }

        return FormatCandidateMember(method, model, position);
    }

    private static string[] MissingTypeUsings(
        Diagnostic diagnostic,
        SemanticModel model,
        Compilation compilation,
        ISymbol within,
        SyntaxToken token,
        Solution? solution,
        CancellationToken cancellationToken)
    {
        var simple = token.Parent as SimpleNameSyntax
            ?? token.Parent?.AncestorsAndSelf().OfType<SimpleNameSyntax>().FirstOrDefault();
        var typeName = simple?.Identifier.ValueText ?? FirstQuotedTerm(diagnostic.GetMessage(CultureInfo.InvariantCulture));
        if (string.IsNullOrEmpty(typeName))
            return [];

        var arity = simple is GenericNameSyntax generic ? generic.Arity : 0;
        var isAttribute = simple is not null && IsInAttribute(simple);
        var searchNames = isAttribute && !typeName.EndsWith("Attribute", StringComparison.Ordinal)
            ? new[] { typeName, typeName + "Attribute" }
            : [typeName];

        var candidates = new List<(int ArityPenalty, int SystemPenalty, string Namespace, string Directive)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void CollectFromCompilation(Compilation comp, bool checkAccessibility)
        {
            var queue = new Queue<INamespaceSymbol>();
            queue.Enqueue(comp.GlobalNamespace);
            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ns = queue.Dequeue();
                foreach (var childNs in ns.GetNamespaceMembers())
                    queue.Enqueue(childNs);

                if (ns.IsGlobalNamespace)
                    continue;

                foreach (var name in searchNames)
                {
                    foreach (var type in ns.GetTypeMembers(name))
                    {
                        if (checkAccessibility && !comp.IsSymbolAccessibleWithin(type, within))
                            continue;
                        if (!checkAccessibility && type.DeclaredAccessibility != Accessibility.Public)
                            continue;
                        var nsName = ns.ToDisplayString();
                        var directive = $"using {nsName};";
                        if (!seen.Add(directive))
                            continue;
                        var arityPenalty = arity == 0 ? (type.Arity == 0 ? 0 : 1) : (type.Arity == arity ? 0 : 1);
                        var systemPenalty = nsName == "System" || nsName.StartsWith("System.", StringComparison.Ordinal) ? 1 : 0;
                        candidates.Add((arityPenalty, systemPenalty, nsName, directive));
                    }
                }
            }
        }

        CollectFromCompilation(compilation, checkAccessibility: true);

        if (solution is not null && candidates.Count == 0)
        {
            foreach (var project in solution.Projects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (project.AssemblyName == compilation.AssemblyName)
                    continue;
                if (project.TryGetCompilation(out var otherComp) && otherComp is not null)
                    CollectFromCompilation(otherComp, checkAccessibility: false);
            }
        }

        return candidates
            .OrderBy(c => c.ArityPenalty)
            .ThenBy(c => c.SystemPenalty)
            .ThenBy(c => c.Namespace, StringComparer.Ordinal)
            .Take(MaxSuggestions)
            .Select(c => c.Directive)
            .ToArray();
    }

    private static bool IsInAttribute(SimpleNameSyntax simple) =>
        simple.Parent is AttributeSyntax
        || (simple.Parent is QualifiedNameSyntax q && q.Parent is AttributeSyntax)
        || (simple.Parent is AliasQualifiedNameSyntax a && a.Parent is AttributeSyntax);

    private static string? UnimplementedInterfaceStub(
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
        var quotedMember = SecondQuotedTerm(message);
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
        var fullDisplay = member.ToDisplayString(SignatureFormat);
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
        var explicitQualifier = needsExplicit ? iface.ToMinimalDisplayString(model, position, SignatureFormat) + "." : "";

        switch (member)
        {
            case IMethodSymbol method:
                var returnType = method.ReturnType.ToMinimalDisplayString(model, position, SignatureFormat);
                var typeParams = method.TypeParameters.IsEmpty
                    ? ""
                    : "<" + string.Join(", ", method.TypeParameters.Select(tp => tp.Name)) + ">";
                var methodParams = string.Join(", ", method.Parameters.Select(p => p.ToMinimalDisplayString(model, position, SignatureFormat)));
                var fullConstraints = method.ToMinimalDisplayString(model, position, StubMemberFormat);
                var whereIdx = fullConstraints.IndexOf(" where ", StringComparison.Ordinal);
                var constraints = whereIdx >= 0 ? fullConstraints[whereIdx..] : "";
                return $"{prefix}{returnType} {explicitQualifier}{method.Name}{typeParams}({methodParams}){constraints} => throw new NotImplementedException();";

            case IPropertySymbol prop:
                var propType = prop.Type.ToMinimalDisplayString(model, position, SignatureFormat);
                var accessors = new List<string>();
                if (prop.GetMethod is not null)
                    accessors.Add("get;");
                if (prop.SetMethod is not null)
                    accessors.Add(prop.SetMethod.IsInitOnly ? "init;" : "set;");
                var accessorText = string.Join(" ", accessors);
                if (prop.IsIndexer)
                {
                    var idxParams = string.Join(", ", prop.Parameters.Select(p => p.ToMinimalDisplayString(model, position, SignatureFormat)));
                    return $"{prefix}{propType} {explicitQualifier}this[{idxParams}] {{ {accessorText} }}";
                }

                return $"{prefix}{propType} {explicitQualifier}{prop.Name} {{ {accessorText} }}";

            case IEventSymbol evt:
                var evtType = evt.Type.ToMinimalDisplayString(model, position, SignatureFormat);
                return $"{prefix}event {evtType} {explicitQualifier}{evt.Name};";

            default:
                return member.ToMinimalDisplayString(model, position, SignatureFormat);
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

    private static ITypeSymbol UnwrapNullable(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments.Length: 1 } nullable)
            return nullable.TypeArguments[0];
        return type;
    }

    private static string? FirstQuotedTerm(string message) => QuotedTerm(message, 0);

    private static string? SecondQuotedTerm(string message) => QuotedTerm(message, 1);

    private static string? QuotedTerm(string message, int index)
    {
        var searchStart = 0;
        for (var i = 0; i <= index; i++)
        {
            var open = message.IndexOf('\'', searchStart);
            if (open < 0)
                return null;
            var close = message.IndexOf('\'', open + 1);
            if (close < 0)
                return null;
            if (i == index)
                return message[(open + 1)..close];
            searchStart = close + 1;
        }

        return null;
    }

    /// <summary>Computes Levenshtein edit distance between two strings under <paramref name="comparison"/>.</summary>
    internal static int LevenshteinDistance(string source, string target, StringComparison comparison)
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

    private sealed record DocumentSnapshot(SemanticModel Model, Compilation Compilation, SourceText Text);
}
