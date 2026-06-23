using DotMake.CommandLine;
using Fuse.Cli.Services;
using Fuse.Fusion;
using Fuse.Plugins.Abstractions.Options;

namespace Fuse.Cli.Commands;

/// <summary>
///     Reduces a caller-supplied set of files, or content piped on stdin, and writes the compacted result to
///     stdout. Unlike <c>fuse dotnet</c>, it does not walk a directory: it compacts exactly what you name.
/// </summary>
[CliCommand(
    Name = "reduce",
    Description = "Reduce specific files (--files) or piped content (--stdin) and print the compacted result to stdout.",
    Parent = typeof(FuseCliCommand))]
public sealed class ReduceCommand
{
    private readonly FusionOrchestrator _orchestrator;
    private readonly Fuse.Collection.Templates.ProjectTemplateRegistry _templateRegistry;
    private readonly IConsoleUI _consoleUI;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ReduceCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>
    ///     Used by DotMake.CommandLine to bind options; the services are <see langword="null" />, so this
    ///     instance must not run.
    /// </remarks>
    public ReduceCommand() : this(null!, null!, null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="ReduceCommand" /> class.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator.</param>
    /// <param name="templateRegistry">The project template registry.</param>
    /// <param name="consoleUI">The console UI for status and error output.</param>
    public ReduceCommand(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        IConsoleUI consoleUI)
    {
        _orchestrator = orchestrator;
        _templateRegistry = templateRegistry;
        _consoleUI = consoleUI;
    }

    /// <summary>
    ///     File paths to reduce, absolute or relative to <see cref="Directory" />.
    /// </summary>
    [CliOption(Required = false, Description = "File paths to reduce (space-separated). Mutually exclusive with --stdin.")]
    public string[]? Files { get; set; }

    /// <summary>
    ///     When set, reads content from standard input instead of files.
    /// </summary>
    [CliOption(Description = "Read content to reduce from stdin instead of files. Mutually exclusive with --files.")]
    public bool Stdin { get; set; }

    /// <summary>
    ///     The file extension that selects the reducer for stdin content.
    /// </summary>
    [CliOption(Description = "Extension that selects the reducer for --stdin content (for example .cs, .ts, .py).")]
    public string Ext { get; set; } = ".cs";

    /// <summary>
    ///     The C# reduction level to apply.
    /// </summary>
    [CliOption(Description = "C# reduction level: none, standard, aggressive, skeleton, publicApi.")]
    public ReductionLevel Level { get; set; } = ReductionLevel.Standard;

    /// <summary>
    ///     Base directory for resolving relative <see cref="Files" /> paths.
    /// </summary>
    [CliOption(Description = "Base directory for resolving relative --files paths.")]
    public string Directory { get; set; } = System.IO.Directory.GetCurrentDirectory();

    /// <summary>
    ///     Optional token ceiling for the emitted output; zero means no limit.
    /// </summary>
    [CliOption(Description = "Maximum tokens the reduced output may use (0 = no limit).")]
    public int MaxTokens { get; set; }

    /// <summary>
    ///     Reduces the requested files or stdin content and writes the result to stdout.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when reduction finishes and the result has been written.</returns>
    public async Task RunAsync(CliContext context)
    {
        var hasFiles = Files is { Length: > 0 };
        if (Stdin && hasFiles)
        {
            _consoleUI.WriteError("Use either --files or --stdin, not both.");
            return;
        }

        int? maxTokens = MaxTokens > 0 ? MaxTokens : null;

        string output;
        if (Stdin)
        {
            var content = await Console.In.ReadToEndAsync(context.CancellationToken);
            output = await ReduceRunner.ReduceContentAsync(
                _orchestrator, _templateRegistry, content, Ext, Level, maxTokens, context.CancellationToken);
        }
        else if (hasFiles)
        {
            output = await ReduceRunner.ReduceFilesAsync(
                _orchestrator, _templateRegistry, Directory, Files!, Level, maxTokens, context.CancellationToken);
        }
        else
        {
            _consoleUI.WriteError("Provide files to reduce with --files, or pipe content with --stdin.");
            return;
        }

        if (output.StartsWith("Error", StringComparison.Ordinal))
        {
            _consoleUI.WriteError(output);
            return;
        }

        // Write the reduced content straight to stdout so the command composes as a filter.
        Console.Out.Write(output);
        if (!output.EndsWith('\n'))
            Console.Out.Write('\n');
    }
}
