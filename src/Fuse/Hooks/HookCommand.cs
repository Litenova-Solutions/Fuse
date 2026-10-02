using System.Text;
using System.Text.Json;
using Fuse.Failures;
using Fuse.Harnesses;
using Fuse.Operations;
using Fuse.Paths;
using Fuse.Protocol;

namespace Fuse.Hooks;

/// <summary>
///     <c>fuse hook &lt;harness&gt; &lt;event&gt;</c>: the one command every registered hook runs. It reads the harness's
///     JSON from stdin, does the work the event asks for, which is the same for every harness, and writes the
///     <see cref="Harness"/>'s answer in that harness's format.
/// </summary>
/// <remarks>
///     A hook must never break the agent's session, so a failure inside Fuse ends with exit code 0 and no output
///     (logged to <c>hook.log</c> in <see cref="RepoRoot.StateDirectory"/>). Only errors introduced and a missing restore
///     are reported; in Claude Code they come with exit code 2, which wakes the agent.
/// </remarks>
internal static class HookCommand
{
    private static readonly string Usage =
        $"usage: fuse hook <{string.Join('|', SupportedHarnesses.All.Select(h => h.Name))}> <{HookEvent.PostEdit}|{HookEvent.PreShell}|{HookEvent.Stop}>";

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length < 2 || SupportedHarnesses.Find(args[0]) is not { } harness || args[1] is not (HookEvent.PostEdit or HookEvent.PreShell or HookEvent.Stop))
        {
            await Console.Error.WriteLineAsync(Usage).ConfigureAwait(false);
            return 0;
        }

        var hookEvent = args[1];
        HookPayload payload;
        try
        {
            payload = HookPayload.Parse(await ReadStdinAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (JsonException e)
        {
            // A payload that will not parse is a harness problem, not the agent's, and the hook still must not break the
            // session; the reason goes to hook.log so this is not a silent no-op.
            Log(Environment.CurrentDirectory, $"{harness.Name} {hookEvent} payload was not valid JSON: {e.Message}");
            return 0;
        }

        // In a Cursor session the hook Fuse registered with Cursor answers, not the one Cursor also runs from this
        // harness's settings.
        if (harness.IsAlsoRunByCursor && payload.FromCursor)
            return 0;

        try
        {
            var answer = hookEvent switch
            {
                HookEvent.PreShell => PreShell(harness, payload),
                HookEvent.PostEdit => await PostEditAsync(harness, payload, cancellationToken).ConfigureAwait(false),
                _ => await StopAsync(harness, payload, cancellationToken).ConfigureAwait(false),
            };
            return Write(answer);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log(payload.Cwd, $"{harness.Name} {hookEvent} failed: {e}");
            return 0;
        }
    }

    /// <summary>
    ///     Rewrites <c>dotnet build</c> and <c>dotnet test</c> in the command the agent is about to run; any other command
    ///     gets no answer. A harness whose answer approves the command gets one only for a command that is one plain
    ///     invocation, rewritten whole (<see cref="Harness.ApprovesRewrittenCommand"/>).
    /// </summary>
    internal static HookAnswer PreShell(Harness harness, HookPayload payload) =>
        payload.Command is { } command
        && (harness.ApprovesRewrittenCommand ? CommandRewriter.RewriteWhole(command) : CommandRewriter.Rewrite(command)) is { } rewritten
            ? harness.ReplaceShellCommand(payload.ToolInput, rewritten)
            : HookAnswer.None;

    private static async Task<HookAnswer> PostEditAsync(Harness harness, HookPayload payload, CancellationToken cancellationToken)
    {
        var edited = payload.EditedFiles().Where(PathRules.IsSource).ToList();
        if (edited.Count == 0)
            return HookAnswer.None;
        var root = RepoRoot.Find(Path.GetDirectoryName(edited[0])!);
        if (root is null)
            return HookAnswer.None;
        // Each file once, however the harness spelled it; the request carries each as an absolute string.
        var files = edited.Select(root.PathOf).Distinct().Select(p => p.Absolute).ToList();

        // A hook the harness runs in the background can wait for a cold load. One it runs inline answers only once the
        // engine is warm and leaves the rest to the stop hook.
        var background = harness.RunsPostEditInBackground;
        var (result, response) = await CheckOperation.RunAsync(
            root, files, waitForLoad: background, background ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(50), cancellationToken).ConfigureAwait(false);
        return ShouldReport(result, response) ? harness.ReportAfterEdit(result.Text) : HookAnswer.None;
    }

    private static async Task<HookAnswer> StopAsync(Harness harness, HookPayload payload, CancellationToken cancellationToken)
    {
        if (payload.StopHookActive)
            return harness.AllowStop();
        var root = RepoRoot.Find(payload.Cwd);
        if (root is null)
            return harness.AllowStop();
        var (result, response) = await CheckOperation.RunAsync(root, null, waitForLoad: true, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        if (!ShouldReport(result, response))
            return harness.AllowStop();
        // A missing restore is not an error the changes introduced; its message already names the command to run.
        return result.Outcome == Outcome.ProblemsFound
            ? harness.BlockStop(result.Text + "\nFix these errors before finishing; HEAD does not have them, so your changes introduced them.")
            : harness.BlockStop(result.Text);
    }

    /// <summary>
    ///     A check that found problems is reported, and so is a missing restore; loading, timeouts and internal failures
    ///     stay silent.
    /// </summary>
    private static bool ShouldReport(OperationResult result, EngineResponse response) =>
        result.Outcome == Outcome.ProblemsFound || response is EngineResponse.Unanswered { Code: ErrorCode.RestoreNeeded };

    /// <summary>Writes <paramref name="answer"/> where the harness reads it and returns its exit code.</summary>
    private static int Write(HookAnswer answer)
    {
        if (answer.StandardOutput.Length > 0)
            Console.Out.Write(answer.StandardOutput);
        if (answer.StandardError.Length > 0)
            Console.Error.Write(answer.StandardError);
        return answer.ExitCode;
    }

    /// <summary>
    ///     Reads the harness's payload from standard input as UTF-8. <see cref="Console.In"/> follows the console input
    ///     code page, which on a Windows console is not UTF-8, so a path with a non-ASCII character arrives as different
    ///     characters and the payload no longer parses. Every harness writes UTF-8; this reads what they write.
    /// </summary>
    private static async Task<string> ReadStdinAsync(CancellationToken cancellationToken)
    {
        await using var input = Console.OpenStandardInput();
        return await ReadUtf8Async(input, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Decodes <paramref name="input"/> as UTF-8, whatever the console's code page says.</summary>
    internal static async Task<string> ReadUtf8Async(Stream input, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(input, Utf8, detectEncodingFromByteOrderMarks: false);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    // throwOnInvalidBytes is false: a byte that is not UTF-8 becomes U+FFFD, because an exception from the decoder would
    // escape the JsonException handler in RunAsync and break the session.
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private static void Log(string cwd, string message)
    {
        try
        {
            var root = RepoRoot.Find(cwd);
            if (root is null)
                return;
            LocalState.RecordRoot(root);
            File.AppendAllText(Path.Combine(root.StateDirectory, "hook.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Logging is the last thing a failing hook does; it must not be the thing that breaks the session.
        }
    }
}
