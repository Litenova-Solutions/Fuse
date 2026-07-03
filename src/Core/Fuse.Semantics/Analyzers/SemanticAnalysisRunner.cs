using Fuse.Indexing;

namespace Fuse.Semantics.Analyzers;

/// <summary>
///     Runs the full set of semantic analyzers over a project and merges their results into one set of nodes,
///     edges, routes, DI registrations, options bindings, and diagnostics.
/// </summary>
/// <remarks>
///     Nodes are deduplicated by id (the last writer wins, which is safe because node records for the same id
///     are equivalent); edges, routes, registrations, and bindings are concatenated. The store deduplicates
///     edges by their derived edge id, so concatenation does not produce duplicate rows.
/// </remarks>
public sealed class SemanticAnalysisRunner
{
    private readonly IReadOnlyList<ISemanticAnalyzer> _analyzers;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SemanticAnalysisRunner" /> class.
    /// </summary>
    /// <param name="analyzers">The analyzers to run, in order.</param>
    public SemanticAnalysisRunner(IEnumerable<ISemanticAnalyzer> analyzers) => _analyzers = analyzers.ToList();

    /// <summary>
    ///     Creates a runner wired with the default analyzer set (interface, DI, constructor injection, MediatR,
    ///     route, options, hosted services, pipeline behaviors, EF Core).
    /// </summary>
    /// <returns>A runner with the standard analyzers.</returns>
    public static SemanticAnalysisRunner CreateDefault()
    {
        var di = new DiRegistrationAnalyzer();
        return new SemanticAnalysisRunner(
        [
            new InterfaceImplementationAnalyzer(),
            di,
            new ConstructorInjectionAnalyzer(di),
            new MediatRAnalyzer(),
            new AspNetRouteAnalyzer(),
            new OptionsBindingAnalyzer(),
            new HostedServiceAnalyzer(),
            new PipelineBehaviorAnalyzer(),
            new EfCoreAnalyzer(),
            new EndpointAnalyzer(),
            new ReferenceEdgeAnalyzer(),
        ]);
    }

    /// <summary>
    ///     Runs every analyzer and merges their output.
    /// </summary>
    /// <param name="context">The analysis context.</param>
    /// <param name="cancellationToken">A token to cancel the analysis.</param>
    /// <returns>The merged result.</returns>
    public SemanticAnalyzerResult Run(SemanticAnalysisContext context, CancellationToken cancellationToken)
    {
        var nodes = new Dictionary<string, NodeRecord>(StringComparer.Ordinal);
        var edges = new List<SemanticEdgeRecord>();
        var routes = new List<RouteRecord>();
        var registrations = new List<DiRegistrationRecord>();
        var bindings = new List<OptionsBindingRecord>();
        var diagnostics = new List<DiagnosticRecord>();

        foreach (var analyzer in _analyzers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = analyzer.Analyze(context, cancellationToken);
            foreach (var node in result.Nodes)
                nodes[node.NodeId] = node;
            edges.AddRange(result.Edges);
            routes.AddRange(result.Routes);
            registrations.AddRange(result.DiRegistrations);
            bindings.AddRange(result.OptionsBindings);
            diagnostics.AddRange(result.Diagnostics);
        }

        return new SemanticAnalyzerResult(nodes.Values.ToList(), edges, routes, registrations, bindings, diagnostics);
    }
}
