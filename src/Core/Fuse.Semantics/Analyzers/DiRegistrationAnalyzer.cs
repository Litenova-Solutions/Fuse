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
        // Keyed DI (G2): AddKeyedSingleton<TService, TImpl>(serviceKey) and friends. The service key is a value
        // argument the generic-2 and typeof extraction paths already ignore (the typeof filter drops the key), so
        // the type-argument forms resolve identically to their non-keyed counterparts.
        ["AddKeyedScoped"] = "Scoped",
        ["AddKeyedSingleton"] = "Singleton",
        ["AddKeyedTransient"] = "Transient",
        ["TryAddKeyedScoped"] = "Scoped",
        ["TryAddKeyedSingleton"] = "Singleton",
        ["TryAddKeyedTransient"] = "Transient",
        // Typed HttpClient (G2 iteration 2, Microsoft.Extensions.Http): AddHttpClient<TClient, TImplementation>()
        // registers TImplementation as the typed client for TClient (a transient resolution), and the single-arg
        // AddHttpClient<TClient>() self-registers the client. The generic-2 and generic-1 paths already extract
        // these; the string/no-arg overloads (named clients) carry no type arguments and produce no edge.
        ["AddHttpClient"] = "Transient",
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
                if (methodName is null)
                    continue;

                // Scrutor decoration: Decorate<TService, TDecorator>() wraps the service with a decorator, a
                // distinct edge from resolution so a reviewer can see the wrapping.
                if (methodName == "Decorate" && nameSyntax is GenericNameSyntax { TypeArgumentList.Arguments.Count: 2 } decorate)
                {
                    var decorated = ResolveType(model, decorate.TypeArgumentList.Arguments[0], cancellationToken);
                    var decorator = ResolveType(model, decorate.TypeArgumentList.Arguments[1], cancellationToken);
                    if (decorated is not null && decorator is not null)
                    {
                        AddNode(nodes, decorated, root);
                        AddNode(nodes, decorator, root);
                        var decSpan = invocation.GetLocation().GetLineSpan();
                        edges.Add(new SemanticEdgeRecord(
                            FromNodeId: SemanticNodes.TypeId(decorated),
                            ToNodeId: SemanticNodes.TypeId(decorator),
                            EdgeType: "di_decorates",
                            Weight: ResolvesToWeight,
                            Confidence: 0.9,
                            Evidence: $"Decorate<{decorated.Name}, {decorator.Name}>",
                            EvidenceFilePath: filePath,
                            EvidenceStartLine: decSpan.StartLinePosition.Line + 1,
                            EvidenceEndLine: decSpan.EndLinePosition.Line + 1));
                    }

                    continue;
                }

                if (!LifetimeByMethod.TryGetValue(methodName, out var lifetime))
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
                // AddScoped<TService>() self-registers the concrete type as itself. When the factory is a lambda
                // whose body constructs a concrete type, recover that implementation so the resolve edge exists.
                if (hasFactoryArgument)
                {
                    var built = ResolveFactoryImplementation(model, invocation, cancellationToken);
                    return (service, built, "factory");
                }

                return (service, service, "generic1");
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

    private static INamedTypeSymbol? ResolveType(SemanticModel model, SyntaxNode typeSyntax, CancellationToken cancellationToken)
    {
        if (model.GetTypeInfo(typeSyntax, cancellationToken).Type is not INamedTypeSymbol type)
            return null;
        // typeof(IRepository<>) resolves to the unbound generic, whose display id ("<>") would not match the
        // type's declaration id ("<T>"); use the original definition so the resolve edge connects to the node.
        return type.IsUnboundGenericType ? type.OriginalDefinition : type;
    }

    // Recovers the concrete type a factory registration builds: the first object creation in the factory
    // lambda's body (for example AddSingleton<IFoo>(sp => new Foo(...))). Returns null when the factory is not
    // a simple lambda that constructs a type.
    private static INamedTypeSymbol? ResolveFactoryImplementation(
        SemanticModel model, InvocationExpressionSyntax invocation, CancellationToken cancellationToken)
    {
        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (argument.Expression is not LambdaExpressionSyntax lambda)
                continue;
            SyntaxNode body = lambda.Body;
            var creation = body.DescendantNodesAndSelf().OfType<ObjectCreationExpressionSyntax>().FirstOrDefault();
            if (creation is not null && model.GetTypeInfo(creation, cancellationToken).Type is INamedTypeSymbol built)
                return built;
        }

        return null;
    }

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
