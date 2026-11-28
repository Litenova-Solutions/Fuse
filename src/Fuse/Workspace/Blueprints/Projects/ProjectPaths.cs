namespace Fuse.Workspace.Blueprints.Projects;

/// <summary>
///     Where one blueprint entry reads its inputs from. Every path is stored repo-relative when inside the
///     repository and absolute otherwise, so a secondary worktree resolves it under its own root.
/// </summary>
internal sealed record ProjectPaths(
    IReadOnlyList<CompilationBlueprint.BlueprintDocument> Compile,
    IReadOnlyList<string> ReferencePath,
    IReadOnlyList<string> Analyzer,
    IReadOnlyList<CompilationBlueprint.BlueprintDocument> AdditionalFiles,
    IReadOnlyList<CompilationBlueprint.BlueprintDocument> AnalyzerConfigFiles,
    string? OutputPath,
    string? OutputRefPath,
    string? CryptoKeyFile);
