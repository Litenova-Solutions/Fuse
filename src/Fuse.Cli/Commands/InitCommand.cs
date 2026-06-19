using DotMake.CommandLine;
using Fuse.Cli.Services;

namespace Fuse.Cli.Commands;

/// <summary>
///     Creates a <c>fuse.json</c> configuration file in the current directory.
/// </summary>
[CliCommand(Name = "init", Description = "Create a fuse.json configuration file in the current directory.", Parent = typeof(FuseCliCommand))]
public sealed class InitCommand
{
    private readonly IConsoleUI _consoleUI;

    /// <summary>
    ///     Initializes a new instance of the <see cref="InitCommand" /> class for CLI binding.
    /// </summary>
    public InitCommand() : this(null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="InitCommand" /> class.
    /// </summary>
    /// <param name="consoleUI">The console UI for status output.</param>
    public InitCommand(IConsoleUI consoleUI)
    {
        _consoleUI = consoleUI;
    }

    /// <summary>
    ///     Writes a starter <c>fuse.json</c> when one does not already exist.
    /// </summary>
    /// <param name="context">The CLI invocation context.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task RunAsync(CliContext context)
    {
        var targetPath = Path.Combine(System.IO.Directory.GetCurrentDirectory(), "fuse.json");
        if (File.Exists(targetPath))
        {
            _consoleUI.WriteError("fuse.json already exists in the current directory.");
            return Task.CompletedTask;
        }

        var template = """
            {
              "directory": ".",
              "output": "./fuse-output",
              "format": "xml",
              "tokenizer": "o200k_base",
              "noManifest": false,
              "provenance": false
            }
            """;

        File.WriteAllText(targetPath, template);
        _consoleUI.WriteSuccess($"Created {targetPath}");
        return Task.CompletedTask;
    }
}
