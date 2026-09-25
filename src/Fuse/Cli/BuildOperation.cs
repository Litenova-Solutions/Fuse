using System.Text;
using Fuse.Dotnet;

namespace Fuse.Cli;

/// <summary>Runs the real <c>dotnet build</c> and prints its errors, or the end of its output when no error line parses.</summary>
internal static class BuildOperation
{
    private const int MaxShown = 20;

    private static readonly string[] QuietArguments = ["-nologo", "-tl:off", "-v:q", "-clp:ErrorsOnly;NoSummary"];

    public static async Task<OperationResult> RunAsync(string workingDirectory, string root, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        var result = await ProcessRunner.RunAsync("dotnet", ["build", .. arguments, .. QuietArguments], workingDirectory, cancellationToken).ConfigureAwait(false);
        var seconds = (Environment.TickCount64 - started) / 1000.0;
        return Render(result, root, seconds, "build");
    }

    internal static OperationResult Render(ProcessResult result, string root, double seconds, string verb)
    {
        var errors = BuildOutputParser.Errors(result.Output, root);
        var text = new StringBuilder();
        foreach (var error in errors.Take(MaxShown))
            text.Append(error).Append('\n');
        if (result.ExitCode == 0)
        {
            text.Append($"fuse: {verb} succeeded in {seconds:0.0} s");
            return new OperationResult(0, text.ToString());
        }

        if (errors.Count == 0)
        {
            // Nothing matched the diagnostic format: show the tail, which is where MSBuild puts the failure.
            var tail = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(30);
            text.Append(string.Join('\n', tail)).Append('\n');
            text.Append($"fuse: {verb} failed (exit code {result.ExitCode}) in {seconds:0.0} s");
            return new OperationResult(1, text.ToString());
        }

        var more = errors.Count > MaxShown ? $", first {MaxShown} shown" : "";
        text.Append($"fuse: {verb} failed with {errors.Count} error(s){more} in {seconds:0.0} s");
        return new OperationResult(1, text.ToString());
    }
}
