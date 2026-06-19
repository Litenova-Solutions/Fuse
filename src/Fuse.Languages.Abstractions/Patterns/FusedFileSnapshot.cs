namespace Fuse.Languages.Abstractions.Patterns;

/// <summary>
///     Minimal file snapshot for cross-file pattern detection.
/// </summary>
public sealed record FusedFileSnapshot(string NormalizedPath, string Content);
