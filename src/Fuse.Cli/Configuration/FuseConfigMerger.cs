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
    public string Directory { get; init; } = System.IO.Directory.GetCurrentDirectory();

    public string Output { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fuse");

    public string? OutputFileName { get; init; }
    public bool? NoManifest { get; init; }
    public bool? Provenance { get; init; }
    public bool? GitStats { get; init; }
    public string? Format { get; init; }
    public string? Tokenizer { get; init; }
    public int? MaxTokens { get; init; }
    public int? SplitTokens { get; init; }
    public bool Recursive { get; init; } = true;
    public bool IncludeMetadata { get; init; } = false;
}

/// <summary>
///     Merges config and CLI values with precedence: flag &gt; config &gt; default.
/// </summary>
public static class FuseConfigMerger
{
    private static readonly string DefaultOutput =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fuse");

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
