namespace Fuse.Plugins.Abstractions.Patterns;

/// <summary>
///     Minimal file snapshot supplied to an <see cref="IPatternDetector" /> for cross-file pattern detection.
/// </summary>
/// <param name="NormalizedPath">Forward-slash normalized path of the file relative to the source root.</param>
/// <param name="Content">Reduced content of the file as it appears in the fused output.</param>
public sealed record FusedFileSnapshot(string NormalizedPath, string Content);
