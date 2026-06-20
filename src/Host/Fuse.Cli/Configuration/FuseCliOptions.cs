namespace Fuse.Cli.Configuration;

/// <summary>
///     CLI-side snapshot of options that may originate from flags or config.
/// </summary>
public sealed class FuseCliOptions
{
    /// <summary>
    ///     Source directory to fuse.
    /// </summary>
    public string Directory { get; init; } = System.IO.Directory.GetCurrentDirectory();

    /// <summary>
    ///     Output directory for fused files.
    /// </summary>
    public string Output { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fuse");

    /// <summary>
    ///     Output file name without extension.
    /// </summary>
    public string? OutputFileName { get; init; }

    /// <summary>
    ///     When <see langword="true" />, omit the manifest header.
    /// </summary>
    public bool? NoManifest { get; init; }

    /// <summary>
    ///     When <see langword="true" />, annotate entries with inclusion provenance.
    /// </summary>
    public bool? Provenance { get; init; }

    /// <summary>
    ///     When <see langword="true" />, include git stats in the manifest.
    /// </summary>
    public bool? GitStats { get; init; }

    /// <summary>
    ///     Output format name (<c>xml</c>, <c>markdown</c>, or <c>json</c>).
    /// </summary>
    public string? Format { get; init; }

    /// <summary>
    ///     Tokenizer model or encoding name.
    /// </summary>
    public string? Tokenizer { get; init; }

    /// <summary>
    ///     Maximum token budget before emission stops.
    /// </summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    ///     Token threshold for splitting output into multiple files.
    /// </summary>
    public int? SplitTokens { get; init; }

    /// <summary>
    ///     Whether to scan subdirectories recursively.
    /// </summary>
    public bool Recursive { get; init; } = true;

    /// <summary>
    ///     Whether to include file metadata in output entries.
    /// </summary>
    public bool IncludeMetadata { get; init; } = false;
}
