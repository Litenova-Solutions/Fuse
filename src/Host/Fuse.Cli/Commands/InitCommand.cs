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
    ///     Initializes a new instance of the <see cref="InitCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>
    ///     Used by DotMake.CommandLine to bind options; the console UI is <see langword="null" />, so this instance
    ///     must not run.
    /// </remarks>
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
    ///     Writes a starter <c>fuse.json</c> to the current directory when one does not already exist.
    /// </summary>
    /// <param name="context">The CLI invocation context.</param>
    /// <returns>A completed task once the file is written or the existing-file warning is reported.</returns>
    /// <remarks>
    ///     Creates <c>fuse.json</c> in the current working directory as a side effect. If the file already exists,
    ///     nothing is written and an error is reported through the console UI.
    /// </remarks>
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
