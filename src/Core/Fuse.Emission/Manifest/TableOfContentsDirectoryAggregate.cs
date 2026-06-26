namespace Fuse.Emission.Manifest;

/// <summary>
///     Per-directory token and file-count totals used when the table of contents collapses to directory rows.
/// </summary>
/// <param name="Directory">The directory path with a trailing slash.</param>
/// <param name="FileCount">The number of files directly under the directory.</param>
/// <param name="Tokens">The sum of per-file token costs in the directory.</param>
internal readonly record struct TableOfContentsDirectoryAggregate(string Directory, int FileCount, long Tokens);
