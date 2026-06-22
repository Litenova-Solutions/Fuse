namespace Fuse.Emission.Models;

/// <summary>
///     Options that control how fused content is emitted to disk or memory.
/// </summary>
public sealed class EmissionOptions
{
    /// <summary>
    ///     The directory where output files are written.
    /// </summary>
    public string OutputDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fuse");

    /// <summary>
    ///     A custom output filename without extension, or <c>null</c> to auto-generate one from the
    ///     output directory name and a timestamp.
    /// </summary>
    public string? OutputFileName { get; set; }

    /// <summary>
    ///     A value indicating whether existing output files are overwritten rather than written alongside
    ///     a timestamped fallback name.
    /// </summary>
    public bool Overwrite { get; set; } = true;

    /// <summary>
    ///     A value indicating whether file size and modification date are included on each output entry.
    /// </summary>
    public bool IncludeMetadata { get; set; } = false;

    /// <summary>
    ///     The hard token limit across all output parts, or <c>null</c> for no limit. Emission halts once
    ///     this limit is exceeded.
    /// </summary>
    public int? MaxTokens { get; set; }

    /// <summary>
    ///     The token threshold at which output is split into a new part, or <c>null</c> to disable splitting.
    /// </summary>
    public int? SplitTokens { get; set; } = 800000;

    /// <summary>
    ///     A value indicating whether the token count is displayed on completion.
    /// </summary>
    public bool ShowTokenCount { get; set; } = true;

    /// <summary>
    ///     A value indicating whether the top token-consuming files are tracked for reporting.
    /// </summary>
    public bool TrackTopTokenFiles { get; set; } = false;

    /// <summary>
    ///     A value indicating whether a manifest header is prepended to output.
    /// </summary>
    public bool IncludeManifest { get; set; } = true;

    /// <summary>
    ///     A value indicating whether git churn stats are included in the manifest.
    /// </summary>
    public bool IncludeGitStats { get; set; } = false;

    /// <summary>
    ///     A value indicating whether inclusion provenance is annotated on each entry.
    /// </summary>
    public bool IncludeProvenance { get; set; } = false;

    /// <summary>
    ///     A value indicating whether identical leading comment headers shared by two or more files are
    ///     replaced with a marker and emitted once in a preamble. Off by default.
    /// </summary>
    public bool DeduplicateHeaders { get; set; } = false;

    /// <summary>
    ///     A value indicating whether member bodies that are byte-identical after normalization across two or
    ///     more files are emitted once and replaced elsewhere by a marker referencing the canonical instance.
    ///     The member signature is always preserved. Off by default.
    /// </summary>
    public bool DeduplicateBodies { get; set; } = false;

    /// <summary>
    ///     An opaque session identifier enabling session-delta emission, or <c>null</c> to disable it. When set,
    ///     files whose identical content was already emitted in this session are omitted on later fusions.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    ///     A value indicating whether to emit a table of contents (a directory tree with per-file token costs
    ///     and a symbol outline) instead of file bodies. A cheap first call for surveying a codebase before
    ///     fetching files in full. Off by default.
    /// </summary>
    public bool TableOfContents { get; set; } = false;

    /// <summary>
    ///     The output serialization format. Defaults to <see cref="OutputFormat.Xml" />.
    /// </summary>
    public OutputFormat Format { get; set; } = OutputFormat.Xml;

    /// <summary>
    ///     The tokenizer model or encoding used for token counting, such as <c>o200k_base</c>.
    /// </summary>
    public string TokenizerModel { get; set; } = Tokenization.TokenizerFactory.DefaultModel;
}
