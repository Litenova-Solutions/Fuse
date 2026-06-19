using Fuse.Cli.Configuration;
using Fuse.Emission.Models;
using Fuse.Emission.Tokenization;
using Fuse.Emission.Writers;

namespace Fuse.Cli.Configuration;

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
