using DotMake.CommandLine;
using Fuse.Cli.Rpc;
using Fuse.Cli.Services;

namespace Fuse.Cli.Commands;

/// <summary>
///     The S3 ambient-verification gate: exits nonzero while the current session has introduced compiler errors it
///     has not resolved, so a Claude Code Stop hook blocks a turn that would end red. It asks the running host for
///     the <c>fuse/check</c> delta and blocks only on errors the session itself introduced (baseline discipline),
///     never a pre-existing repo error. With no host serving the root it exits 0, so a dirty or un-hosted repo is
///     never walled in.
/// </summary>
[CliCommand(
    Name = "gate",
    Description = "Ambient-verification gate: exit nonzero (with the red summary) while this session has introduced compiler errors, for use as a Stop hook. Exits 0 when clean, when only pre-existing errors remain, or when no host is serving.",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class GateCommand
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>The workspace directory. Defaults to the current directory.</summary>
    [CliArgument(Description = "The workspace directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>The check-session id whose introduced errors gate the turn.</summary>
    [CliOption(Name = "--session", Description = "The check-session id (defaults to 'hook').")]
    public string Session { get; set; } = "hook";

    /// <summary>
    ///     Runs the gate: sets a nonzero exit code and prints the red summary when the session introduced errors.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the verdict has been decided.</returns>
    public async Task RunAsync(CliContext context)
    {
        var root = System.IO.Path.GetFullPath(Path);
        var delta = await FuseHostClient.TryCheckDeltaAsync(root, Session, ConnectTimeout, context.CancellationToken);
        if (delta is null || !AmbientVerification.IsRed(delta))
            return; // No host, or nothing the session introduced is red: allow the turn to end (exit 0).

        // The process exit code is Environment.ExitCode on normal termination (the top-level program returns void),
        // so setting it here makes `fuse gate` exit nonzero and a Stop hook block the red turn.
        Console.Error.WriteLine(AmbientVerification.RenderGateBlock(delta));
        Environment.ExitCode = 1;
    }
}
