using Fuse.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Semantics.Analyzers;

/// <summary>
///     Discovers dependency-injection registrations and the service-to-implementation mapping they establish.
/// </summary>
/// <remarks>
///     Supports the generic forms <c>AddScoped&lt;TService, TImplementation&gt;()</c>,
///     <c>AddScoped&lt;TService&gt;()</c> (self-registration), and the <c>typeof</c> pair
///     <c>AddScoped(typeof(IFoo), typeof(Foo))</c>, across Scoped/Singleton/Transient and their
///     <c>TryAdd*</c> variants. Each registration produces a <see cref="DiRegistrationRecord" />; when both
///     service and implementation are known, a <c>service -&gt; implementation : di_resolves_to</c> edge
///     (weight 0.95) is emitted so a resolver can answer "what implements this service". Factory and instance
///     registrations record the service with an unknown implementation and no resolve edge.
/// </remarks>
public sealed class DiRegistrationAnalyzer : ISemanticAnalyzer
{
    private const double ResolvesToWeight = 0.95;

    private static readonly Dictionary<string, string> LifetimeByMethod = new(StringComparer.Ordinal)
    {
        ["AddScoped"] = "Scoped",
        ["AddSingleton"] = "Singleton",
        ["AddTransient"] = "Transient",
        ["TryAddScoped"] = "Scoped",
        ["TryAddSingleton"] = "Singleton",
        ["TryAddTransient"] = "Transient",
    };

    /// <inheritdoc />
    public SemanticAnalyzerResult Analyze(SemanticAnalysisContext context, CancellationToken cancellationToken)
    {
        var compilation = context.Project.Compilation;
        var root = context.RootDirectory;
        var nodes = new Dictionary<string, NodeRecord>(StringComparer.Ordinal);
        var edges = new List<SemanticEdgeRecord>();
        var registrations = new List<DiRegistrationRecord>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = compilation.GetSemanticModel(tree);
            var filePath = NormalizeRelative(root, tree.FilePath);

            foreach (var invocation in tree.GetRoot(cancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var nameSyntax = GetNameSyntax(invocation);
                var methodName = nameSyntax?.Identifier.ValueText;
                if (methodName is null || !LifetimeByMethod.TryGetValue(methodName, out var lifetime))
                    continue;

                var (service, implementation, kind) = ResolveTypes(model, invocation, nameSyntax!, cancellationToken);
                if (service is null)
                    continue;

                var span = invocation.GetLocation().GetLineSpan();
                var startLine = span.StartLinePosition.Line + 1;
                var endLine = span.EndLinePosition.Line + 1;

                registrations.Add(new DiRegistrationRecord(
                    RegistrationId: $"di:{filePath}:{startLine}:{service.Name}",
                    ServiceName: service.ToDisplayString(SemanticNodes.NodeNameFormat),
                    Lifetime: lifetime,
                    FilePath: filePath,
                    StartLine: startLine,
                    EndLine: endLine,
                    RegistrationKind: kind,
                    Confidence: implementation is null ? 0.70 : 0.95,
                    ServiceSymbolId: SymbolIdBuilder.Build(service),
                    ImplementationSymbolId: implementation is null ? null : SymbolIdBuilder.Build(implementation),
                    ImplementationName: implementation?.ToDisplayString(SemanticNodes.NodeNameFormat),
                    Evidence: invocation.ToString()));

                if (implementation is not null)
                {
                    AddNode(nodes, service, root);
                    AddNode(nodes, implementation, root);
                    edges.Add(new SemanticEdgeRecord(
                        FromNodeId: SemanticNodes.TypeId(service),
                        ToNodeId: SemanticNodes.TypeId(implementation),
                        EdgeType: "di_resolves_to",
                        Weight: ResolvesToWeight,
                        Confidence: 0.95,
                        Evidence: $"{methodName}<{service.Name}, {implementation.Name}>",
                        EvidenceFilePath: filePath,
                        EvidenceStartLine: startLine,
                        EvidenceEndLine: endLine));
                }
            }
        }

        return new SemanticAnalyzerResult(nodes.Values.ToList(), edges, [], registrations, [], []);
    }

    // Resolves the (service, implementation, kind) from a registration call. Returns service=null when the call
    // is not a recognized registration shape.
    private static (INamedTypeSymbol? Service, INamedTypeSymbol? Implementation, string Kind) ResolveTypes(
        SemanticModel model,
        InvocationExpressionSyntax invocation,
        SimpleNameSyntax nameSyntax,
        CancellationToken cancellationToken)
    {
        if (nameSyntax is GenericNameSyntax generic)
        {
            var typeArgs = generic.TypeArgumentList.Arguments;
            var hasFactoryArgument = invocation.ArgumentList.Arguments.Count > 0;
            if (typeArgs.Count >= 2)
                return (ResolveType(model, typeArgs[0], cancellationToken), ResolveType(model, typeArgs[1], cancellationToken), "generic2");
            if (typeArgs.Count == 1)
            {
                var service = ResolveType(model, typeArgs[0], cancellationToken);
                // AddScoped<TService>(factory) registers TService with an implementation the factory builds;
                // AddScoped<TService>() self-registers the concrete type as itself.
                return hasFactoryArgument ? (service, null, "factory") : (service, service, "generic1");
            }
        }

        // typeof pair: AddScoped(typeof(IFoo), typeof(Foo)).
        var typeofArgs = invocation.ArgumentList.Arguments
            .Select(a => a.Expression)
            .OfType<TypeOfExpressionSyntax>()
            .ToList();
        if (typeofArgs.Count >= 2)
            return (ResolveType(model, typeofArgs[0].Type, cancellationToken), ResolveType(model, typeofArgs[1].Type, cancellationToken), "typeof");

        return (null, null, "unknown");
    }

    private static INamedTypeSymbol? ResolveType(SemanticModel model, SyntaxNode typeSyntax, CancellationToken cancellationToken) =>
        model.GetTypeInfo(typeSyntax, cancellationToken).Type as INamedTypeSymbol;

    private static SimpleNameSyntax? GetNameSyntax(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name,
        SimpleNameSyntax name => name,
        _ => null,
    };

    private static void AddNode(Dictionary<string, NodeRecord> nodes, INamedTypeSymbol type, string root)
    {
        var node = SemanticNodes.TypeNode(type, root);
        nodes[node.NodeId] = node;
    }

    private static string NormalizeRelative(string rootDirectory, string absolutePath) =>
        string.IsNullOrEmpty(absolutePath)
            ? absolutePath
            : Path.GetRelativePath(rootDirectory, absolutePath).Replace(Path.DirectorySeparatorChar, '/');
}
