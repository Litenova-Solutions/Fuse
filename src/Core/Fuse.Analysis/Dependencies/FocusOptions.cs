namespace Fuse.Analysis.Dependencies;

/// <summary>
///     Options for dependency-aware focus scoping.
/// </summary>
/// <param name="Seed">Type name, filename, or relative directory path.</param>
/// <param name="Depth">Traversal depth; 1 = seed plus direct dependencies.</param>
public sealed record FocusOptions(string Seed, int Depth = 1);
