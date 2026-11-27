namespace Fuse.Workspace.Blueprints;

/// <summary>
///     The project references one blueprint entry keeps, with target framework identity for multi-targeted
///     targets. The load resolves each against the projects it opens in the same batch.
/// </summary>
internal sealed record ProjectReferences(
    IReadOnlyList<CompilationBlueprint.BlueprintProjectReference> Items);
