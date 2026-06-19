namespace Fuse.Emission.Models;

/// <summary>
///     Options that control how fused content is emitted to disk or memory.
/// </summary>
public sealed class EmissionOptions
{
    /// <summary>
    ///     Gets or sets the directory where output files are written.
    /// </summary>
    public string OutputDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fuse");

    /// <summary>
    ///     Gets or sets a custom output filename without extension, or <c>null</c> to auto-generate.
    /// </summary>
    public string? OutputFileName { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether existing output files are overwritten.
    /// </summary>
    public bool Overwrite { get; set; } = true;

    /// <summary>
    ///     Gets or sets a value indicating whether file size and modification date are included in output.
    /// </summary>
    public bool IncludeMetadata { get; set; } = false;

    /// <summary>
    ///     Gets or sets the hard token limit across all output parts, or <c>null</c> for no limit.
    /// </summary>
    public int? MaxTokens { get; set; }

    /// <summary>
    ///     Gets or sets the token threshold at which output is split into a new part, or <c>null</c> to disable splitting.
    /// </summary>
    public int? SplitTokens { get; set; } = 800000;

    /// <summary>
    ///     Gets or sets a value indicating whether the token count is displayed on completion.
    /// </summary>
    public bool ShowTokenCount { get; set; } = true;

    /// <summary>
    ///     Gets or sets a value indicating whether the top token-consuming files are tracked.
    /// </summary>
    public bool TrackTopTokenFiles { get; set; } = false;

    /// <summary>
    ///     Gets or sets a value indicating whether a manifest header is prepended to output.
    /// </summary>
    public bool IncludeManifest { get; set; } = true;

    /// <summary>
    ///     Gets or sets a value indicating whether git churn stats are included in the manifest.
    /// </summary>
    public bool IncludeGitStats { get; set; } = false;

    /// <summary>
    ///     Gets or sets a value indicating whether inclusion provenance is annotated on each entry.
    /// </summary>
    public bool IncludeProvenance { get; set; } = false;

    /// <summary>
    ///     Gets or sets the output serialization format.
    /// </summary>
    public OutputFormat Format { get; set; } = OutputFormat.Xml;

    /// <summary>
    ///     Gets or sets the tokenizer model or encoding used for token counting.
    /// </summary>
    public string TokenizerModel { get; set; } = Tokenization.TokenizerFactory.DefaultModel;
}
