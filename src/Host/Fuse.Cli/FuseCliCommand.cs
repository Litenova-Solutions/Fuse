using DotMake.CommandLine;
using Fuse.Cli.Services;

namespace Fuse.Cli;

/// <summary>
///     Root Fuse CLI command. Fuse V3 is a .NET semantic context engine; the root prints guidance and parents
///     the V3 subcommands (index, map, localize, resolve, context, review, diagnostics, find, reduce, init,
///     models, mcp). It no longer runs generic template fusion.
/// </summary>
[CliCommand(Description = "Fuse: a .NET semantic context engine for AI agents.")]
public class FuseCliCommand
{
    private readonly IConsoleUI _consoleUI;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FuseCliCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the console UI is null, so this instance must not run.</remarks>
    public FuseCliCommand() : this(null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="FuseCliCommand" /> class.
    /// </summary>
    /// <param name="consoleUI">The console UI for guidance output.</param>
    public FuseCliCommand(IConsoleUI consoleUI) => _consoleUI = consoleUI;

    /// <summary>
    ///     Prints guidance pointing to the V3 workflow when no subcommand is given.
    /// </summary>
    /// <param name="context">The CLI invocation context.</param>
    public void Run(CliContext context)
    {
        _consoleUI.WriteResult(
            """
            Fuse is a .NET semantic context engine. Start by indexing a workspace, then query it:

              fuse index [path]                 Build the persistent semantic index.
              fuse map [path]                   Print the workspace map (symbols, routes).
              fuse review --changed-since ref   Review the semantic impact of a change.
              fuse resolve --service IFoo       Resolve wiring (service/request/route/config/symbol).
              fuse localize --task "..."        Localize a task to candidate files.
              fuse context --seed Foo           Plan and emit context for seeds.
              fuse find <query>                 Exact symbol/path/text lookup.
              fuse diagnostics [path]           Report index state.

            Run 'fuse <command> --help' for options. 'fuse mcp serve' starts the MCP server.
            """);
    }
}
