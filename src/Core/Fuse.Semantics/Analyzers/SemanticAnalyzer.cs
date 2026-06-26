using Fuse.Indexing;

namespace Fuse.Semantics.Analyzers;

/// <summary>
///     The output of a semantic analyzer: the graph nodes and typed edges it discovered, plus any routes,
///     DI registrations, options bindings, and diagnostics.
/// </summary>
/// <param name="Nodes">Nodes referenced by the edges (so endpoints exist before edges are stored).</param>
/// <param name="Edges">Typed, weighted edges.</param>
/// <param name="Routes">Routes discovered (empty for most analyzers).</param>
/// <param name="DiRegistrations">DI registrations discovered (empty for most analyzers).</param>
/// <param name="OptionsBindings">Options bindings discovered (empty for most analyzers).</param>
/// <param name="Diagnostics">Diagnostics emitted during analysis.</param>
public sealed record SemanticAnalyzerResult(
    IReadOnlyList<NodeRecord> Nodes,
    IReadOnlyList<SemanticEdgeRecord> Edges,
    IReadOnlyList<RouteRecord> Routes,
    IReadOnlyList<DiRegistrationRecord> DiRegistrations,
    IReadOnlyList<OptionsBindingRecord> OptionsBindings,
    IReadOnlyList<DiagnosticRecord> Diagnostics)
{
    /// <summary>An empty result.</summary>
    public static SemanticAnalyzerResult Empty { get; } = new([], [], [], [], [], []);

    /// <summary>Creates a result carrying only nodes and edges.</summary>
    /// <param name="nodes">The nodes.</param>
    /// <param name="edges">The edges.</param>
    /// <returns>A result with empty route, DI, options, and diagnostic lists.</returns>
    public static SemanticAnalyzerResult FromGraph(
        IReadOnlyList<NodeRecord> nodes,
        IReadOnlyList<SemanticEdgeRecord> edges) =>
        new(nodes, edges, [], [], [], []);
}

/// <summary>
///     The input to a semantic analyzer: a loaded project and the workspace root.
/// </summary>
/// <param name="Project">The loaded project with its compilation.</param>
/// <param name="RootDirectory">The workspace root, used to make file paths relative.</param>
public sealed record SemanticAnalysisContext(LoadedProject Project, string RootDirectory);

/// <summary>
///     Discovers a category of semantic edges (interface implementation, DI, MediatR, routes, options, and so
///     on) from a loaded project.
/// </summary>
public interface ISemanticAnalyzer
{
    /// <summary>
    ///     Analyzes a project and returns the nodes and edges it discovered.
    /// </summary>
    /// <param name="context">The analysis context.</param>
    /// <param name="cancellationToken">A token to cancel the analysis.</param>
    /// <returns>The discovered nodes, edges, and related records.</returns>
    SemanticAnalyzerResult Analyze(SemanticAnalysisContext context, CancellationToken cancellationToken);
}
