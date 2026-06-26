using System.Text;
using DotMake.CommandLine;
using Fuse.Cli.Services;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Retrieval;

namespace Fuse.Cli.Commands;

/// <summary>
///     Reviews the semantic impact of a change: the changed files, the blast radius (callers, DI consumers,
///     route and request handlers, options consumers, tests), and why each non-changed file is included. Reads
///     the persistent index; run <c>index</c> first. Source rendering is added in a later phase.
/// </summary>
[CliCommand(
    Name = "review",
    Description = "Review the semantic impact of a change since a git base ref.",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class ReviewCommand
{
    private readonly IConsoleUI _consoleUI;
    private readonly IChangeSource _changeSource;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ReviewCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the dependencies are null, so this instance must not run.</remarks>
    public ReviewCommand() : this(null!, null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="ReviewCommand" /> class.
    /// </summary>
    /// <param name="consoleUI">The console UI for output.</param>
    /// <param name="changeSource">The change source for resolving the git base ref.</param>
    public ReviewCommand(IConsoleUI consoleUI, IChangeSource changeSource)
    {
        _consoleUI = consoleUI;
        _changeSource = changeSource;
    }

    /// <summary>The workspace directory. Defaults to the current directory.</summary>
    [CliArgument(Description = "The workspace directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>The git base ref to diff against.</summary>
    [CliOption(Name = "--changed-since", Description = "The git base ref to diff against (branch, commit, or HEAD~N).")]
    public string ChangedSince { get; set; } = "HEAD";

    /// <summary>The token budget.</summary>
    [CliOption(Name = "--max-tokens", Required = false, Description = "Token budget.")]
    public int? MaxTokens { get; set; }

    /// <summary>Whether to include related test files.</summary>
    [CliOption(Name = "--include-tests", Description = "Include related test files.")]
    public bool IncludeTests { get; set; } = true;

    /// <summary>
    ///     Runs the review command.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the review has been written.</returns>
    public async Task RunAsync(CliContext context)
    {
        var root = System.IO.Path.GetFullPath(Path);
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        if (!File.Exists(databasePath))
        {
            _consoleUI.WriteError($"No index found at {databasePath}. Run 'fuse index' first.");
            return;
        }

        await using var store = new WorkspaceIndexStore(databasePath);
        await store.InitializeAsync(context.CancellationToken);

        var request = new ReviewRequest(root, ChangedSince, MaxTokens: MaxTokens, IncludeTests: IncludeTests);
        var plan = await new SemanticRetrievalEngine(store, _changeSource).ReviewAsync(request, context.CancellationToken);

        var output = new StringBuilder();
        output.Append(ReviewPreambleBuilder.Build(plan, ChangedSince));
        output.AppendLine();
        output.AppendLine("plan:");
        foreach (var item in plan.Items)
        {
            var keep = item.MustKeep ? "*" : " ";
            output.AppendLine($"  {keep} {item.Score:F3} [{item.Role}/{item.Tier}] {item.Path}  (~{item.EstimatedTokens} tokens)");
        }

        _consoleUI.WriteResult(output.ToString());
    }
}
