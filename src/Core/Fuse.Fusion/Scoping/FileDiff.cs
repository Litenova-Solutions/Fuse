namespace Fuse.Fusion.Scoping;

/// <summary>
///     A single file's unified diff: its path, added and removed line counts, and the raw hunk text.
/// </summary>
/// <param name="Path">The normalized, forward-slash relative path of the changed file.</param>
/// <param name="Added">The number of added lines across all hunks.</param>
/// <param name="Removed">The number of removed lines across all hunks.</param>
/// <param name="Hunks">The unified-diff hunk text (the <c>@@</c> blocks), or an empty string when none.</param>
public sealed record FileDiff(string Path, int Added, int Removed, string Hunks);
