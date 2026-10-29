using System.Globalization;

namespace Fuse.Operations;

/// <summary>What <c>fuse build</c> was told: the arguments for <c>dotnet build</c> and Fuse's own.</summary>
/// <param name="Passthrough">The arguments for <c>dotnet build</c>, unchanged.</param>
/// <param name="MaxErrors">How many errors to show: 0 for the default count, <see cref="int.MaxValue"/> for every error.</param>
/// <param name="Json">True for one JSON object per diagnostic instead of text.</param>
/// <param name="ListWarnings">True to list the warnings after the errors.</param>
/// <param name="FromRewrite">True when the pre-shell hook rewrote the command, which prints a notice line first.</param>
internal sealed record BuildArguments(IReadOnlyList<string> Passthrough, int MaxErrors, bool Json, bool ListWarnings, bool FromRewrite)
{
    /// <summary>The most errors <c>--max-errors</c> takes; past it the count is a typo, and <c>--all-errors</c> shows every error.</summary>
    internal const int MaxErrorsLimit = 100000;

    /// <summary>Fuse's own options: every other token goes to <c>dotnet build</c>, except a near miss of one of these, which is refused.</summary>
    private static readonly string[] FuseOptions = ["--max-errors", "--all-errors", "--json", "--warnings", "--help"];

    /// <summary>
    ///     Parses <paramref name="args"/>. Help tokens are dropped: the command prints help before parsing, so one here
    ///     never reaches a build. After <c>--</c> every token goes to <c>dotnet build</c>, even one that looks like a Fuse
    ///     option.
    /// </summary>
    /// <returns>The arguments, or the usage failure to print when they do not parse.</returns>
    public static (BuildArguments? Arguments, string? Refusal) Parse(IReadOnlyList<string> args)
    {
        var passthrough = new List<string>();
        var maxErrors = 0;
        var allErrors = false;
        var json = false;
        var warnings = false;
        var fromRewrite = false;
        var options = true;
        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (token == "--" && options)
            {
                options = false;
                continue;
            }

            if (!options)
            {
                passthrough.Add(token);
                continue;
            }

            if (token is "--help" or "-h" or "--from-rewrite")
            {
                // The rewrite token is the hook's: it is read here and never forwarded to dotnet.
                fromRewrite |= token == "--from-rewrite";
                continue;
            }

            if (token == "--json")
            {
                json = true;
                continue;
            }

            if (token == "--warnings")
            {
                warnings = true;
                continue;
            }

            if (token == "--all-errors")
            {
                allErrors = true;
                continue;
            }

            if (token == "--max-errors" || token.StartsWith("--max-errors=", StringComparison.Ordinal))
            {
                var refusal = TakeCount(token, args, ref i, out var count);
                if (refusal is not null)
                    return (null, refusal);
                maxErrors = count;
                continue;
            }

            if (NearMiss.Suggest(token, FuseOptions) is { } suggestion)
                return (null, $"fuse: '{token}' is not a fuse build option; did you mean '{suggestion}'?");
            passthrough.Add(token);
        }

        if (allErrors && maxErrors > 0)
            return (null, "fuse: --all-errors and --max-errors cannot be combined; choose one");
        return (new BuildArguments(passthrough, allErrors ? int.MaxValue : maxErrors, json, warnings, fromRewrite), null);
    }

    /// <summary>The options the build runs with: JSON and every error uncap the errors shown.</summary>
    public BuildOptions ToOptions() =>
        new(Json || MaxErrors == int.MaxValue ? int.MaxValue : MaxErrors > 0 ? MaxErrors : BuildOptions.Default.MaxShown, Json, ListWarnings);

    /// <summary>The count <c>--max-errors</c> takes, from the token after it or after <c>=</c>, or the refusal to print when it takes none.</summary>
    private static string? TakeCount(string token, IReadOnlyList<string> args, ref int i, out int count)
    {
        count = 0;
        var text = token.StartsWith("--max-errors=", StringComparison.Ordinal) ? token["--max-errors=".Length..] : ++i < args.Count ? args[i] : null;
        if (text is null || text == "0")
            return "fuse: --max-errors takes a count of 1 or more; use --all-errors for every error";
        if (!int.TryParse(text, CultureInfo.InvariantCulture, out count) || count < 1 || count > MaxErrorsLimit)
            return $"fuse: --max-errors takes a count of 1 to {MaxErrorsLimit}, not '{text}'";
        return null;
    }
}
