using DotMake.CommandLine;
using Fuse.Cli.Services;

namespace Fuse.Cli.Commands;

[CliCommand(Name = "init", Description = "Create a fuse.json configuration file in the current directory.", Parent = typeof(FuseCliCommand))]
public sealed class InitCommand
{
    private readonly IConsoleUI _consoleUI;

    public InitCommand() : this(null!)
    {
    }

    public InitCommand(IConsoleUI consoleUI)
    {
        _consoleUI = consoleUI;
    }

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
