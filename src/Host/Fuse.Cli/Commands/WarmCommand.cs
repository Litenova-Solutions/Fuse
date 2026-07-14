using DotMake.CommandLine;
using Fuse.Cli.Mcp;
using Fuse.Cli.Services;
using Fuse.Semantics;

namespace Fuse.Cli.Commands;

/// <summary>
///     Warms the persistent index for a workspace now (R38): builds the syntax-first index so a subsequent read
///     hits a warm store rather than paying the cold cost. Unlike the automatic eager warm-on-start, this is
///     explicit and runs regardless of the <c>FUSE_EAGER_INDEX</c> opt-out. A warm store is a no-op.
/// </summary>
[CliCommand(
    Name = "warm",
    Description = "Warm the persistent index for a workspace now, so the next read is fast (syntax-first build; a warm store is a no-op).",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class WarmCommand
{
    private readonly SemanticIndexer _indexer;
    private readonly IConsoleUI _consoleUI;

    /// <summary>
    ///     Initializes a new instance of the <see cref="WarmCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the dependencies are null, so this instance must not run.</remarks>
    public WarmCommand() : this(null!, null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="WarmCommand" /> class.
    /// </summary>
    /// <param name="indexer">The semantic indexer used to build the index.</param>
    /// <param name="consoleUI">The console UI for output.</param>
    public WarmCommand(SemanticIndexer indexer, IConsoleUI consoleUI)
    {
        _indexer = indexer;
        _consoleUI = consoleUI;
    }

    /// <summary>The workspace directory to warm. Defaults to the current directory.</summary>
    [CliArgument(Description = "The workspace directory to warm. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>
    ///     The always-on warm service action (R40): <c>install</c>, <c>uninstall</c>, or <c>status</c>. Opt-in and
    ///     never installed by <c>fuse mcp install</c>. Empty for a one-shot warm of <see cref="Path" />.
    /// </summary>
    [CliOption(Name = "--service", Description = "Manage the opt-in always-on warm service: install, uninstall, or status.")]
    public string Service { get; set; } = "";

    /// <summary>
    ///     Runs the warm command.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the index has been warmed.</returns>
    public async Task RunAsync(CliContext context)
    {
        if (!string.IsNullOrWhiteSpace(Service))
        {
            RunServiceAction(Service.Trim().ToLowerInvariant());
            return;
        }

        var root = System.IO.Path.GetFullPath(Path);
        if (!Directory.Exists(root))
        {
            _consoleUI.WriteError($"Directory not found: {root}");
            return;
        }

        _consoleUI.WriteStep($"Warming the index for {root}");
        await EagerIndex.WarmAsync(_indexer, root, context.CancellationToken);
        _consoleUI.WriteResult($"warmed: {root} (syntax-first index built; the semantic upgrade continues in the background).");
    }

    // R40: the opt-in always-on warm service surface. Reports the platform-native register/unregister command and
    // the first-run notice. The service is store-backed, LRU-capped, and battery-aware (WarmService); it is never
    // installed by `fuse mcp install`.
    private void RunServiceAction(string action)
    {
        var invocation = Environment.ProcessPath ?? "fuse";
        switch (action)
        {
            case "install":
                _consoleUI.WriteResult(
                    $"warm service ({WarmServiceDefinition.PlatformMechanism()}): opt-in, store-backed, LRU-capped at {WarmServiceLru.DefaultCap} repos, battery-aware." + Environment.NewLine +
                    $"register with: {WarmServiceDefinition.InstallCommand(invocation)}" + Environment.NewLine +
                    WarmServiceDefinition.FirstRunNotice());
                break;
            case "uninstall":
                _consoleUI.WriteResult(
                    $"warm service uninstall ({WarmServiceDefinition.PlatformMechanism()}): {WarmServiceDefinition.UninstallCommand()}");
                break;
            case "status":
            default:
                _consoleUI.WriteResult(
                    $"warm service: opt-in ({WarmServiceDefinition.PlatformMechanism()}); install with 'fuse warm --service install', remove with 'fuse warm --service uninstall'. Never installed by 'fuse mcp install'.");
                break;
        }
    }
}
