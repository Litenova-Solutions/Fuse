namespace Fuse.Plugins.Abstractions.Scoping;

/// <summary>
///     A ranked file and its relevance score.
/// </summary>
/// <param name="Path">The normalized relative path of the ranked file.</param>
/// <param name="Score">The relevance score; higher is more relevant.</param>
public sealed record RankedFile(string Path, double Score);
