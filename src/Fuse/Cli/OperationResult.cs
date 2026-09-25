namespace Fuse.Cli;

/// <summary>What a Fuse operation prints and the exit code it returns. The same result feeds the CLI, the hooks and the MCP tools.</summary>
/// <param name="ExitCode">0 clean, 1 errors or failed tests found, 2 Fuse could not answer.</param>
/// <param name="Text">The complete output, already capped.</param>
internal sealed record OperationResult(int ExitCode, string Text)
{
    public bool Found => ExitCode == 1;

    public bool Failed => ExitCode == 2;
}
