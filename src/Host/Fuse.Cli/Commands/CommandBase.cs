using DotMake.CommandLine;
using Fuse.Fusion.Scoping;
using Fuse.Cli.Services;
using Fuse.Collection.Models;
using Fuse.Collection.Templates;
using Fuse.Cli.Configuration;
using Fuse.Emission.Models;
using Fuse.Emission.Serialization;
using Fuse.Emission.Tokenization;
using Fuse.Emission.Writers;
using Fuse.Fusion;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Plugins.Abstractions.Skeleton;

namespace Fuse.Cli.Commands;

/// <summary>
///     Abstract base for all Fuse CLI commands. Holds the shared fusion options bound by DotMake.CommandLine
///     and the services needed to build and run a fusion request.
/// </summary>
/// <remarks>
///     Concrete commands expose their own <c>[CliCommand]</c> attribute and a <c>RunAsync</c> entry point, build a
///     <see cref="FusionRequestBuilder" /> through <see cref="CreateRequestBuilder" />, and dispatch via
///     <see cref="ExecuteFusionAsync" />. The parameterless DI-binding constructor used by the CLI framework passes
///     null services; those instances exist only for option binding and must not invoke fusion.
/// </remarks>
public abstract class CommandBase
{
    /// <summary>
    ///     The <see cref="FusionOrchestrator" /> that runs the collection, reduction, and emission pipeline.
    /// </summary>
    protected readonly FusionOrchestrator _orchestrator;

    /// <summary>
    ///     The <see cref="ProjectTemplateRegistry" /> used to resolve a template's default file extensions.
    /// </summary>
    protected readonly ProjectTemplateRegistry _templateRegistry;

    /// <summary>
    ///     Registry that resolves <see cref="ISkeletonExtractor" /> instances by file extension, used to warn when
    ///     skeleton mode has no extractor for a template's file types.
    /// </summary>
    protected readonly CapabilityRegistry<ISkeletonExtractor> _skeletonExtractors;

    /// <summary>
    ///     The console UI used to write status, results, and errors. In MCP mode this is a stderr-only sink.
    /// </summary>
    protected readonly IConsoleUI _consoleUI;

    /// <summary>
    ///     Initializes a new instance of the <see cref="CommandBase" /> class.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator that runs the pipeline.</param>
    /// <param name="templateRegistry">The project template registry.</param>
    /// <param name="skeletonExtractors">Skeleton extractors resolved by file extension.</param>
    /// <param name="consoleUI">The console UI for status output.</param>
    protected CommandBase(
        FusionOrchestrator orchestrator,
        ProjectTemplateRegistry templateRegistry,
        CapabilityRegistry<ISkeletonExtractor> skeletonExtractors,
        IConsoleUI consoleUI)
    {
        _orchestrator = orchestrator;
        _templateRegistry = templateRegistry;
        _skeletonExtractors = skeletonExtractors;
        _consoleUI = consoleUI;
    }

    /// <summary>
    ///     Runs a fusion request once, displays results, and optionally watches for changes to re-run.
    /// </summary>
    /// <param name="request">The fusion request to execute.</param>
    /// <param name="cancellationToken">Token used to cancel the run and stop watching.</param>
    /// <returns>A task that completes when the run finishes, or when watch mode is cancelled.</returns>
    /// <remarks>
    ///     Writes output files (or in-memory content) through the orchestrator and reports progress, results, and
    ///     errors via the console UI. <see cref="FusionValidationException" />, <see cref="FusionException" />, the
    ///     cancellation triggered by <paramref name="cancellationToken" />, and other exceptions are caught and
    ///     surfaced as console error messages rather than propagated. When <see cref="Watch" /> is set it blocks
    ///     until cancelled, re-running fusion after edits settle; watch mode is disabled automatically when stdio
    ///     is redirected (MCP).
    /// </remarks>
    protected async Task ExecuteFusionAsync(FusionRequest request, CancellationToken cancellationToken)
    {
        if (Watch && StdioGuard.IsStdioRedirected())
        {
            _consoleUI.WriteStep("Warning: Watch mode is disabled when stdin/stdout are redirected (MCP stdio).");
            Watch = false;
        }

        try
        {
            EmitAgenticWarnings(request);
            await RunFusionOnceAsync(request, cancellationToken);

            if (!Watch || cancellationToken.IsCancellationRequested)
                return;

            _consoleUI.WriteStep("Watching for file changes...");

            using var watcher = new DebouncedFileWatcher(
                request.Collection.SourceDirectory,
                request.Collection.Recursive);

            watcher.Changed += async _ =>
            {
                _consoleUI.WriteStep("Change detected, re-running fusion...");
                await RunFusionOnceAsync(request, cancellationToken);
            };

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (FusionValidationException ex)
        {
            foreach (var error in ex.Errors)
            {
                _consoleUI.WriteError(error);
            }
        }
        catch (FusionException ex)
        {
            _consoleUI.WriteError($"Error: {ex.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _consoleUI.WriteError($"Error: {ex.Message}");
            if (ex.InnerException is not null)
            {
                _consoleUI.WriteError($"  {ex.InnerException.Message}");
            }
        }
    }

    private async Task RunFusionOnceAsync(FusionRequest request, CancellationToken cancellationToken)
    {
        var result = await _orchestrator.FuseAsync(request, cancellationToken);

        if (TryHandleEmptyResult(request, result))
            return;

        DisplayResults(result, request.Emission);
        WriteRunReport(result, request.Emission);
    }

    private void WriteRunReport(FusionResult result, EmissionOptions emissionOptions)
    {
        if (string.IsNullOrWhiteSpace(Report))
            return;

        var json = RunReportBuilder.Build(result, emissionOptions);

        if (Report == "-")
        {
            Console.Out.WriteLine(json);
            return;
        }

        File.WriteAllText(Report, json);
        _consoleUI.WriteResult($"Report: {ConsoleUI.GetFriendlyPath(Path.GetFullPath(Report))}");
    }

    /// <summary>
    ///     Creates a fusion request builder preconfigured from the shared CLI options and any discovered config.
    /// </summary>
    /// <param name="template">The project template to apply, or <see langword="null" /> to leave the template unset.</param>
    /// <returns>
    ///     A <see cref="FusionRequestBuilder" /> populated with directory, collection, emission, reduction, and
    ///     change options. Call <see cref="FusionRequestBuilder.Build" /> to produce the request.
    /// </returns>
    /// <remarks>
    ///     Loads the nearest <c>fuse.json</c> via <see cref="FuseConfigLoader" />; explicit CLI flags take
    ///     precedence over config values, which take precedence over defaults.
    /// </remarks>
    protected FusionRequestBuilder CreateRequestBuilder(ProjectTemplate? template = null)
    {
        var config = FuseConfigLoader.Load(Directory);
        var emission = BuildEmissionOptions(config);

        var builder = new FusionRequestBuilder(_templateRegistry)
            .WithSourceDirectory(ResolveDirectory(config))
            .WithMaxFileSizeKb(MaxFileSize)
            .WithCollectionBehavior(
                Recursive,
                IgnoreBinary,
                ExcludeEmptyFiles,
                ExcludeAutoGenerated,
                ExcludeTestProjects,
                excludeUnitTestProjects: false,
                RespectGitIgnore)
            .WithEmissionOptions(emission)
            .WithReductionOptions(new ReductionOptions(
                enableRedaction: !NoRedact,
                includeRedactReport: RedactReport))
            .WithReductionCacheOptions(useCache: !NoCache, clearCache: ClearCache);

        if (template.HasValue)
        {
            builder.WithTemplate(template.Value);
        }

        if (OnlyExtensions?.Length > 0)
        {
            builder.WithOnlyExtensions(OnlyExtensions);
        }

        if (Parallelism > 0)
        {
            builder.WithParallelism(Parallelism);
        }

        if (IncludeExtensions?.Length > 0)
        {
            builder.WithIncludeExtensions(IncludeExtensions);
        }

        if (ExcludeExtensions?.Length > 0)
        {
            builder.WithExcludeExtensions(ExcludeExtensions);
        }

        if (ExcludeDirectories?.Length > 0)
        {
            builder.WithExcludeDirectories(ExcludeDirectories);
        }

        if (ExcludeFiles?.Length > 0)
        {
            builder.WithExcludeFiles(ExcludeFiles);
        }

        if (ExcludePatterns?.Length > 0)
        {
            builder.WithExcludePatterns(ExcludePatterns);
        }

        if (!string.IsNullOrWhiteSpace(ChangedSince))
        {
            builder.WithChangeOptions(new ChangeOptions(ChangedSince, IncludeDependents, Review));
        }

        return builder;
    }

    private EmissionOptions BuildEmissionOptions(FuseConfig? config)
    {
        var defaultOutput = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fuse");
        var outputDirectory = Output;
        if (config?.Output is not null && Output == defaultOutput)
            outputDirectory = config.Output;

        return new EmissionOptions
        {
            OutputDirectory = outputDirectory,
            OutputFileName = OutputFileName ?? config?.Name,
            Overwrite = Overwrite,
            IncludeMetadata = IncludeMetadata,
            MaxTokens = MaxTokens ?? config?.MaxTokens,
            SplitTokens = SplitTokens ?? config?.SplitTokens ?? 800000,
            ShowTokenCount = ShowTokenCount,
            TrackTopTokenFiles = TrackTopTokenFiles,
            IncludeManifest = !(NoManifest || (config?.NoManifest ?? false)),
            IncludeGitStats = GitStats || (config?.GitStats ?? false),
            IncludeProvenance = Provenance || (config?.Provenance ?? false),
            TokenizerModel = Tokenizer ?? config?.Tokenizer ?? TokenizerFactory.DefaultModel,
            Format = EntryFormatterFactory.ParseFormat(Format ?? config?.Format),
            DeduplicateHeaders = DedupHeaders,
            TableOfContents = TableOfContents,
        };
    }

    private string ResolveDirectory(FuseConfig? config)
    {
        if (config?.Directory is not null &&
            Directory == System.IO.Directory.GetCurrentDirectory())
        {
            return config.Directory;
        }

        return Directory;
    }

    private void EmitAgenticWarnings(FusionRequest request)
    {
        if (!request.Reduction.SkeletonMode)
            return;

        var template = request.Collection.Template;
        if (template is null)
            return;

        var extensions = _templateRegistry.GetTemplate(template.Value).Extensions;
        var hasSkeletonSupport = extensions.Any(ext => _skeletonExtractors.TryResolve(ext) is not null);
        if (hasSkeletonSupport)
            return;

        _consoleUI.WriteStep(
            "Warning: Skeleton mode is requested but no skeleton extractor is registered for this template's file types.");
    }

    private bool TryHandleEmptyResult(FusionRequest request, FusionResult result)
    {
        if (result.ProcessedFileCount > 0)
            return false;

        if (request.Changes is not null)
        {
            var since = request.Changes.Since;
            if (!request.InMemory)
            {
                _consoleUI.WriteStep(
                    $"Warning: No files changed since '{since}' were found in the collected file set.");
            }
            else if (!string.IsNullOrEmpty(result.InMemoryContent))
            {
                _consoleUI.WriteResult(result.InMemoryContent);
            }

            return true;
        }

        _consoleUI.WriteError("No files found matching the criteria. Aborting.");
        return true;
    }

    private void DisplayResults(FusionResult result, EmissionOptions emissionOptions)
    {
        _consoleUI.WriteSuccess("Fusion complete!");

        foreach (var path in result.GeneratedPaths)
        {
            var fileInfo = new FileInfo(path);
            var friendlyPath = ConsoleUI.GetFriendlyPath(path);
            _consoleUI.WriteResult($"Output: {friendlyPath}");
        }

        if (emissionOptions.ShowTokenCount)
        {
            var totalSizeKB = result.GeneratedPaths.Sum(p => new FileInfo(p).Length) / 1024.0;
            var tokensFormatted = result.TotalTokens >= 1000
                ? $"{result.TotalTokens / 1000.0:F0}k"
                : $"{result.TotalTokens}";

            var statsLine =
                $"Stats:  {totalSizeKB:F0} KB | {tokensFormatted} tokens | {result.ProcessedFileCount}/{result.TotalFileCount} files | {result.Duration.TotalSeconds:F1}s";

            if (result.ReductionCacheHits > 0 || result.ReductionCacheMisses > 0)
            {
                statsLine += $" | cache: {result.ReductionCacheHits} hit / {result.ReductionCacheMisses} miss";
            }

            _consoleUI.WriteResult(statsLine);

            if (emissionOptions.TrackTopTokenFiles && result.TopTokenFiles.Count > 0)
            {
                _consoleUI.WriteResult("\nTop Token Consumers:");
                for (var i = 0; i < result.TopTokenFiles.Count; i++)
                {
                    var file = result.TopTokenFiles[i];
                    var count = file.Count >= 1000
                        ? $"{file.Count / 1000.0:F1}k"
                        : file.Count.ToString();
                    _consoleUI.WriteResult($"{i + 1}. {file.Path} ({count})");
                }
            }
        }
    }

    #region Test Project Options

    /// <summary>
    ///     Exclude common test project directories.
    /// </summary>
    [CliOption(Description = "Exclude common test project directories.")]
    public bool ExcludeTestProjects { get; set; } = false;

    #endregion

    #region Extension Override Options

    /// <summary>
    ///     Additional file extensions to include alongside template defaults.
    /// </summary>
    [CliOption(Required = false, Description = "Additional file extensions to include alongside template defaults (e.g., .txt,.log).")]
    public string[]? IncludeExtensions { get; set; }

    /// <summary>
    ///     File extensions to remove from template defaults.
    /// </summary>
    [CliOption(Required = false, Description = "File extensions to remove from template defaults (e.g., .xml,.md).")]
    public string[]? ExcludeExtensions { get; set; }

    /// <summary>
    ///     Comma-separated extensions to fuse exclusively, ignoring template defaults.
    /// </summary>
    [CliOption(Required = false, Description = "Fuse ONLY the specified comma-separated file extensions, ignoring all template defaults.")]
    public string[]? OnlyExtensions { get; set; }

    #endregion

    #region Directory Options

    /// <summary>
    ///     Path to the directory to process.
    /// </summary>
    [CliOption(Description = "Path to the directory to process.")]
    public string Directory { get; set; } = System.IO.Directory.GetCurrentDirectory();

    /// <summary>
    ///     Path to the output directory.
    /// </summary>
    [CliOption(Description = "Path to the output directory.")]
    public string Output { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fuse");

    /// <summary>
    ///     Directory names to exclude from scanning.
    /// </summary>
    [CliOption(Required = false, Description = "Directory names to exclude from scanning (e.g., Migrations, wwwroot).")]
    public string[]? ExcludeDirectories { get; set; }

    #endregion

    #region Output Options

    /// <summary>
    ///     Output file name without extension.
    /// </summary>
    [CliOption(Name = "name", Required = false, Description = "Name of the output file (without extension).")]
    public string? OutputFileName { get; set; }

    /// <summary>
    ///     Overwrite the output file when it already exists.
    /// </summary>
    [CliOption(Description = "Overwrite the output file if it exists.")]
    public bool Overwrite { get; set; } = true;

    #endregion

    #region Search Options

    /// <summary>
    ///     Search subdirectories recursively.
    /// </summary>
    [CliOption(Description = "Search recursively through subdirectories.")]
    public bool Recursive { get; set; } = true;

    /// <summary>
    ///     Maximum file size in KB to process (0 for unlimited).
    /// </summary>
    [CliOption(Description = "Maximum file size in KB to process (0 for unlimited).")]
    public int MaxFileSize { get; set; } = 0;

    /// <summary>
    ///     Skip binary files during collection.
    /// </summary>
    [CliOption(Description = "Ignore binary files.")]
    public bool IgnoreBinary { get; set; } = true;

    /// <summary>
    ///     Maximum degree of parallelism for pipeline stages.
    /// </summary>
    [CliOption(Description = "Maximum degree of parallelism for pipeline stages (default: processor count).")]
    public int Parallelism { get; set; } = Environment.ProcessorCount;

    #endregion

    #region Content Options

    /// <summary>
    ///     Include file metadata in output entries.
    /// </summary>
    [CliOption(Description = "Include file metadata (size, dates) in the output.")]
    public bool IncludeMetadata { get; set; } = false;

    /// <summary>
    ///     Apply <c>.gitignore</c> rules found in the directory tree.
    /// </summary>
    [CliOption(Description = "Respect rules from .gitignore files found in the directory tree.")]
    public bool RespectGitIgnore { get; set; } = true;

    #endregion

    #region Token Options

    /// <summary>
    ///     Stop emission when this token count is reached.
    /// </summary>
    [CliOption(Description = "Stops processing completely when token count is reached.")]
    public int? MaxTokens { get; set; }

    /// <summary>
    ///     Split output when this token count per part is exceeded.
    /// </summary>
    [CliOption(Description = "Split output into multiple files when this token count is exceeded.")]
    public int? SplitTokens { get; set; } = 800000;

    /// <summary>
    ///     Display the final token count after fusion completes.
    /// </summary>
    [CliOption(Description = "Displays the final estimated token count upon completion.")]
    public bool ShowTokenCount { get; set; } = true;

    /// <summary>
    ///     Display the top five token-consuming files after fusion.
    /// </summary>
    [CliOption(Description = "Tracks and displays the top 5 files consuming the most tokens.")]
    public bool TrackTopTokenFiles { get; set; } = false;

    /// <summary>
    ///     Omit the manifest header from output.
    /// </summary>
    [CliOption(Description = "Disable the manifest header prepended to output.")]
    public bool NoManifest { get; set; } = false;

    /// <summary>
    ///     Emit a table of contents (directory tree, symbol outline, per-file token costs) instead of file
    ///     bodies. A cheap first call for surveying a codebase before fetching files in full.
    /// </summary>
    [CliOption(Name = "toc", Description = "Emit a table of contents (tree, symbol outline, per-file token costs) instead of file bodies.")]
    public bool TableOfContents { get; set; } = false;

    /// <summary>
    ///     Include git churn and last-modified stats in the manifest.
    /// </summary>
    [CliOption(Description = "Include git churn and last-modified stats in the manifest.")]
    public bool GitStats { get; set; } = false;

    /// <summary>
    ///     Annotate entries with dependency inclusion provenance.
    /// </summary>
    [CliOption(Description = "Annotate entries with dependency inclusion provenance.")]
    public bool Provenance { get; set; } = false;

    /// <summary>
    ///     Replace identical leading comment headers shared across files with a marker, emitted once.
    /// </summary>
    [CliOption(Description = "Replace identical leading comment headers shared across files with a marker, emitted once.")]
    public bool DedupHeaders { get; set; } = false;

    /// <summary>
    ///     Output format (<c>xml</c>, <c>markdown</c>, <c>json</c>, or <c>compact</c>).
    /// </summary>
    [CliOption(Required = false, Description = "Output format: xml, markdown, json, or compact.")]
    public string? Format { get; set; }

    /// <summary>
    ///     Tokenizer model or encoding name.
    /// </summary>
    [CliOption(Required = false, Description = "Tokenizer model or encoding (default: o200k_base).")]
    public string? Tokenizer { get; set; }

    /// <summary>
    ///     Write a machine-readable JSON run report to the given path, or to stdout when set to <c>-</c>.
    /// </summary>
    [CliOption(Required = false, Description = "Write a machine-readable JSON run report to a file path, or to stdout with '-'.")]
    public string? Report { get; set; }

    #endregion

    #region Security Options

    /// <summary>
    ///     Disable secret redaction before emission.
    /// </summary>
    [CliOption(Description = "Disable secret redaction (redaction is on by default).")]
    public bool NoRedact { get; set; } = false;

    /// <summary>
    ///     Append a redaction count summary to the output.
    /// </summary>
    [CliOption(Description = "Append a redaction count summary to the output.")]
    public bool RedactReport { get; set; } = false;

    #endregion

    #region Exclusion Options

    /// <summary>
    ///     Specific file names to exclude from fusion.
    /// </summary>
    [CliOption(Required = false, Description = "Exclude specific file names (e.g., appsettings.Development.json).")]
    public string[]? ExcludeFiles { get; set; }

    /// <summary>
    ///     Glob patterns for files to exclude from fusion.
    /// </summary>
    [CliOption(Required = false, Description = "Exclude files matching glob patterns (e.g., **/Migrations/**, **/*.min.js).")]
    public string[]? ExcludePatterns { get; set; }

    /// <summary>
    ///     Skip zero-byte files during collection.
    /// </summary>
    [CliOption(Description = "Skip empty (zero-byte) files.")]
    public bool ExcludeEmptyFiles { get; set; } = true;

    /// <summary>
    ///     Skip files containing an auto-generated marker in the first few lines.
    /// </summary>
    [CliOption(Description = "Skip files containing an auto-generated code marker in the first few lines.")]
    public bool ExcludeAutoGenerated { get; set; } = true;

    /// <summary>
    ///     Git ref used to scope fusion to changed files.
    /// </summary>
    [CliOption(Required = false, Description = "Git ref (branch, commit, HEAD~N) to scope fusion to changed files.")]
    public string? ChangedSince { get; set; }

    /// <summary>
    ///     Include first-degree dependents of changed files when scoping by git ref.
    /// </summary>
    [CliOption(Description = "Include first-degree dependents of changed files.")]
    public bool IncludeDependents { get; set; } = true;

    /// <summary>
    ///     Prepend a review map (per-changed-file diff hunks and direct callers) when scoping by git ref.
    /// </summary>
    [CliOption(Description = "Review shape: prepend diff hunks and direct callers for each changed file (use with --changed-since).")]
    public bool Review { get; set; } = false;

    #endregion

    #region Cache and Watch Options

    /// <summary>
    ///     Disable per-file reduction caching.
    /// </summary>
    [CliOption(Description = "Disable per-file reduction caching.")]
    public bool NoCache { get; set; } = false;

    /// <summary>
    ///     Clear the <c>.fuse/cache</c> directory before running.
    /// </summary>
    [CliOption(Description = "Clear the .fuse/cache directory before running.")]
    public bool ClearCache { get; set; } = false;

    /// <summary>
    ///     Re-run fusion when source files change after edits settle.
    /// </summary>
    [CliOption(Description = "Watch for file changes and re-run fusion after edits settle.")]
    public bool Watch { get; set; } = false;

    #endregion
}
