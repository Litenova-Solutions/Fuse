using DotMake.CommandLine;
using Fuse.Cli.Rpc;
using Fuse.Cli.Services;

namespace Fuse.Cli.Commands;

/// <summary>
///     The S3 ambient-verification delta command: prints the compiler diagnostics the current session's on-disk
///     edits introduced or resolved since its baseline, by asking the running <c>fuse host</c> / <c>fuse mcp
///     serve</c> for the <c>fuse/check</c> delta. Designed to run from a Claude Code PostToolUse hook after every
///     Edit/Write: it emits nothing on an empty delta (no transcript spam) and exits 0 silently when no resident
///     host serves the root, so it never blocks editing and never runs a build.
/// </summary>
[CliCommand(
    Name = "check",
    Description = "Ambient verification: print the diagnostics this session's edits introduced or resolved since the baseline, from the running host. Silent when nothing changed or no host is serving. Never builds.",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class CheckCommand
{
    // The hook connects to an already-running host, so a fresh CLI process pays only its own start plus this probe;
    // --fast trims the probe further. A live local host connects well inside either bound.
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FastConnectTimeout = TimeSpan.FromMilliseconds(150);

    /// <summary>The workspace directory. Defaults to the current directory.</summary>
    [CliArgument(Description = "The workspace directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>Delta mode (the only mode): the diagnostics introduced or resolved since the session baseline.</summary>
    [CliOption(Name = "--delta", Description = "Report the diagnostics introduced or resolved since the session baseline (the default and only mode).")]
    public bool Delta { get; set; }

    /// <summary>The check-session id whose baseline the delta is measured against.</summary>
    [CliOption(Name = "--session", Description = "The check-session id (defaults to 'hook'; the store is per-repository, so one id per repo is enough).")]
    public string Session { get; set; } = "hook";

    /// <summary>Use the shorter connect probe suited to a hook on the hot edit path.</summary>
    [CliOption(Name = "--fast", Description = "Use a shorter connect probe (hook mode).")]
    public bool Fast { get; set; }

    /// <summary>
    ///     Runs the ambient-verification delta command.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the delta (if any) has been printed.</returns>
    public async Task RunAsync(CliContext context)
    {
        var root = System.IO.Path.GetFullPath(Path);
        var timeout = Fast ? FastConnectTimeout : DefaultConnectTimeout;
        var delta = await FuseHostClient.TryCheckDeltaAsync(root, Session, timeout, context.CancellationToken);
        if (delta is null)
            return; // No host serving the root: exit 0 silently so a hook never blocks editing.

        var text = AmbientVerification.RenderDelta(delta);
        if (text.Length > 0)
            Console.Out.WriteLine(text);
    }
}
