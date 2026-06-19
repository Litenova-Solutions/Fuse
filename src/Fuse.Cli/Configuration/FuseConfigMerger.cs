using Fuse.Cli.Configuration;
using Fuse.Emission.Models;
using Fuse.Emission.Tokenization;
using Fuse.Emission.Writers;

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

/// <summary>
///     Merges config and CLI values with precedence: flag &gt; config &gt; default.
/// </summary>
public static class FuseConfigMerger
{
    private static readonly string DefaultOutput =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fuse");

    /// <summary>
    ///     Merges optional <paramref name="config" /> values into CLI-provided <paramref name="cli" /> options.
    /// </summary>
    /// <param name="config">Loaded <c>fuse.json</c> values, if any.</param>
    /// <param name="cli">CLI flag values for the current invocation.</param>
    /// <returns>Merged options with flag precedence over config defaults.</returns>
    public static FuseCliOptions Merge(FuseConfig? config, FuseCliOptions cli)
    {
        return new FuseCliOptions
        {
            Directory = cli.Directory != System.IO.Directory.GetCurrentDirectory() || config?.Directory is null
                ? cli.Directory
                : config.Directory,
            Output = cli.Output != DefaultOutput || config?.Output is null
                ? cli.Output
                : config.Output,
            OutputFileName = cli.OutputFileName ?? config?.Name,
            NoManifest = cli.NoManifest ?? config?.NoManifest,
            Provenance = cli.Provenance ?? config?.Provenance,
            GitStats = cli.GitStats ?? config?.GitStats,
            Format = cli.Format ?? config?.Format,
            Tokenizer = cli.Tokenizer ?? config?.Tokenizer,
            MaxTokens = cli.MaxTokens ?? config?.MaxTokens,
            SplitTokens = cli.SplitTokens ?? config?.SplitTokens,
            Recursive = cli.Recursive,
            IncludeMetadata = cli.IncludeMetadata,
        };
    }

    /// <summary>
    ///     Applies merged CLI options onto an existing <see cref="EmissionOptions" /> instance.
    /// </summary>
    /// <param name="options">Merged CLI options.</param>
    /// <param name="baseOptions">The emission options to update in place.</param>
    /// <returns><paramref name="baseOptions" /> after applying merged CLI values.</returns>
    public static EmissionOptions BuildEmissionOptions(FuseCliOptions options, EmissionOptions baseOptions)
    {
        baseOptions.IncludeManifest = !(options.NoManifest ?? false);
        baseOptions.IncludeProvenance = options.Provenance ?? false;
        baseOptions.IncludeGitStats = options.GitStats ?? false;
        baseOptions.TokenizerModel = options.Tokenizer ?? TokenizerFactory.DefaultModel;

        if (!string.IsNullOrWhiteSpace(options.Format))
            baseOptions.Format = EntryFormatterFactory.ParseFormat(options.Format);

        if (options.MaxTokens.HasValue)
            baseOptions.MaxTokens = options.MaxTokens;

        if (options.SplitTokens.HasValue)
            baseOptions.SplitTokens = options.SplitTokens;

        baseOptions.IncludeMetadata = options.IncludeMetadata;
        return baseOptions;
    }
}
