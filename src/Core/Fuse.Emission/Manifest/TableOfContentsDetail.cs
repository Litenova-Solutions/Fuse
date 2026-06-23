namespace Fuse.Emission.Manifest;

/// <summary>
///     The level of detail rendered in a table of contents. Lower levels emit less, letting a caller fit the
///     document under a size budget by degrading rather than truncating.
/// </summary>
public enum TableOfContentsDetail
{
    /// <summary>Every file with its token cost and full symbol outline.</summary>
    Full,

    /// <summary>Every file with its token cost, but no symbol outline.</summary>
    PathsOnly,

    /// <summary>One row per directory with a file count and aggregate token cost; individual files are omitted.</summary>
    Directories,
}
