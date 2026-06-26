using Fuse.Indexing;

namespace Fuse.Semantics.Analyzers;

/// <summary>
///     Builds node records for synthetic graph endpoints that are not a single source type: a framework service
///     contract (for example <c>IHostedService</c> or <c>IPipelineBehavior</c>) that several concrete types
///     register against. These give edges a stable, named endpoint so a resolver can ask "what registers
///     against this contract".
/// </summary>
public static class SyntheticNodes
{
    /// <summary>
    ///     Builds a node for a named service contract, keyed by <c>service:{name}</c>.
    /// </summary>
    /// <param name="name">The service contract simple name.</param>
    /// <returns>A node record with kind <c>service</c> and no source location.</returns>
    public static NodeRecord Service(string name) => new(
        NodeId: SemanticNodes.ServiceId(name),
        Kind: "service",
        DisplayName: name,
        StableKey: name,
        FilePath: null);
}
