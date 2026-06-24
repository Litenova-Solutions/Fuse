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
    ///     Gets or sets the rerank model to act on: <c>bi</c> (the default bi-encoder) or <c>cross</c> (the
    ///     cross-encoder used when <c>FUSE_RERANK_MODEL=cross</c>).
    /// </summary>
    [CliOption(Description = "Rerank model: bi (default bi-encoder) or cross (cross-encoder).")]
    public string Model { get; set; } = "bi";

    /// <summary>
    ///     Runs the selected model action.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the action finishes.</returns>
    public async Task RunAsync(CliContext context)
    {
        var modelId = ResolveModelId();
        if (modelId is null)
        {
            _consoleUI.WriteError($"Unknown model '{Model}'. Use bi or cross.");
            return;
        }

        switch (Action.Trim().ToLowerInvariant())
        {
            case "status":
                ReportStatus(modelId);
                break;
            case "download":
                await DownloadAsync(modelId, context.CancellationToken);
                break;
            case "remove":
                Remove(modelId);
                break;
            default:
                _consoleUI.WriteError($"Unknown action '{Action}'. Use status, download, or remove.");
                break;
        }
    }

    // Maps the user-facing bi/cross alias to a cache model id, or null for an unknown alias.
    private string? ResolveModelId() => Model.Trim().ToLowerInvariant() switch
    {
        "bi" or "" => RerankModelLocator.ModelId,
        "cross" => RerankModelLocator.CrossEncoderModelId,
        _ => null,
    };

    private void ReportStatus(string modelId)
    {
        var present = RerankModelLocator.IsModelPresent(modelId);
        var sizeMb = RerankModelDownloader.FilesFor(modelId).Sum(f => f.Bytes) / 1_000_000;
        _consoleUI.WriteResult(
            $"Rerank model ({modelId}): {(present ? "present" : "not downloaded")}");
        _consoleUI.WriteStep($"Cache directory: {RerankModelLocator.ModelDirectory(modelId)}");
        if (!present)
        {
            var enableHint = modelId == RerankModelLocator.CrossEncoderModelId
                ? "then set FUSE_RERANK=1 and FUSE_RERANK_MODEL=cross to enable cross-encoder reranking."
                : "then set FUSE_RERANK=1 to enable dense reranking on the query path.";
            _consoleUI.WriteStep(
                $"Run 'fuse models download --model {Model}' to fetch it (~{sizeMb} MB), {enableHint}");
        }
    }

    private async Task DownloadAsync(string modelId, CancellationToken cancellationToken)
    {
        var sizeMb = RerankModelDownloader.FilesFor(modelId).Sum(f => f.Bytes) / 1_000_000;
        _consoleUI.WriteStep(
            $"Downloading {modelId} (~{sizeMb} MB) to {RerankModelLocator.ModelDirectory(modelId)}");

        try
        {
            var progress = new Progress<string>(line => _consoleUI.WriteStep(line));
            await RerankModelDownloader.DownloadAsync(progress, cancellationToken, modelId);
            var enableHint = modelId == RerankModelLocator.CrossEncoderModelId
                ? "Set FUSE_RERANK=1 and FUSE_RERANK_MODEL=cross to enable cross-encoder reranking "
                : "Set FUSE_RERANK=1 to enable reranking on the query path ";
            _consoleUI.WriteSuccess(
                $"Rerank model ready. {enableHint}(opt-in; the lexical path remains the default).");
        }
        catch (Exception ex)
        {
            _consoleUI.WriteError($"Model download failed: {ex.Message}");
        }
    }

    private void Remove(string modelId)
    {
        _consoleUI.WriteResult(
            RerankModelDownloader.Remove(modelId)
                ? $"Removed the rerank model from {RerankModelLocator.ModelDirectory(modelId)}."
                : "No matching rerank model is cached.");
    }
}
