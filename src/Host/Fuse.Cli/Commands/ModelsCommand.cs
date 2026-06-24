using DotMake.CommandLine;
using Fuse.Cli.Services;
using Fuse.Plugins.Rerank.Onnx;

namespace Fuse.Cli.Commands;

/// <summary>
///     Manages the optional on-disk models that back opt-in features, currently the dense rerank embedder
///     (all-MiniLM-L6-v2). Downloading is always explicit: scoping never fetches a model mid-run.
/// </summary>
[CliCommand(
    Name = "models",
    Description = "Manage optional models for opt-in features (the dense rerank embedder): status, download, remove.",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class ModelsCommand
{
    private readonly IConsoleUI _consoleUI;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ModelsCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the console UI is <see langword="null" />, so this instance must not run.</remarks>
    public ModelsCommand() : this(null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="ModelsCommand" /> class.
    /// </summary>
    /// <param name="consoleUI">The console UI for status output.</param>
    public ModelsCommand(IConsoleUI consoleUI)
    {
        _consoleUI = consoleUI;
    }

    /// <summary>
    ///     Gets or sets the action: <c>status</c> (default), <c>download</c>, or <c>remove</c>.
    /// </summary>
    [CliArgument(Description = "Action: status (default), download, or remove.")]
    public string Action { get; set; } = "status";

    /// <summary>
    ///     Runs the selected model action.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the action finishes.</returns>
    public async Task RunAsync(CliContext context)
    {
        switch (Action.Trim().ToLowerInvariant())
        {
            case "status":
                ReportStatus();
                break;
            case "download":
                await DownloadAsync(context.CancellationToken);
                break;
            case "remove":
                Remove();
                break;
            default:
                _consoleUI.WriteError($"Unknown action '{Action}'. Use status, download, or remove.");
                break;
        }
    }

    private void ReportStatus()
    {
        var present = RerankModelLocator.IsModelPresent();
        _consoleUI.WriteResult(
            $"Dense rerank model ({RerankModelLocator.ModelId}): {(present ? "present" : "not downloaded")}");
        _consoleUI.WriteStep($"Cache directory: {RerankModelLocator.ModelDirectory()}");
        if (!present)
        {
            _consoleUI.WriteStep(
                $"Run 'fuse models download' to fetch it (~{RerankModelDownloader.TotalBytes / 1_000_000} MB), "
                + "then set FUSE_RERANK=1 to enable dense reranking on the query path.");
        }
    }

    private async Task DownloadAsync(CancellationToken cancellationToken)
    {
        _consoleUI.WriteStep(
            $"Downloading {RerankModelLocator.ModelId} (~{RerankModelDownloader.TotalBytes / 1_000_000} MB) "
            + $"to {RerankModelLocator.ModelDirectory()}");

        try
        {
            var progress = new Progress<string>(line => _consoleUI.WriteStep(line));
            await RerankModelDownloader.DownloadAsync(progress, cancellationToken);
            _consoleUI.WriteSuccess(
                "Dense rerank model ready. Set FUSE_RERANK=1 to enable reranking on the query path "
                + "(opt-in; the lexical path remains the default).");
        }
        catch (Exception ex)
        {
            _consoleUI.WriteError($"Model download failed: {ex.Message}");
        }
    }

    private void Remove()
    {
        _consoleUI.WriteResult(
            RerankModelDownloader.Remove()
                ? $"Removed the dense rerank model from {RerankModelLocator.ModelDirectory()}."
                : "No dense rerank model is cached.");
    }
}
