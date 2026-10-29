using System.Globalization;

namespace Fuse.Operations;

/// <summary>What <c>fuse check</c> was told: which files to check and how many errors to show.</summary>
/// <param name="Files">The files, directories and patterns as named, or empty for every change.</param>
/// <param name="MaxErrors">How many errors to show: 0 for the default count, <see cref="int.MaxValue"/> for every error.</param>
/// <param name="Json">True for one JSON object per error instead of text.</param>
internal sealed record CheckArguments(IReadOnlyList<string> Files, int MaxErrors, bool Json)
{
    /// <summary>The most errors <c>--max-errors</c> takes; past it the count is a typo, and <c>--all-errors</c> shows every error.</summary>
    internal const int MaxErrorsLimit = 100000;

    /// <summary>
    ///     Parses <paramref name="args"/>. Help tokens are dropped: the command prints help before parsing, so one here
    ///     never reaches a check. After <c>--</c> every token is a file, even one that looks like an option.
    /// </summary>
    /// <returns>The arguments, or the usage failure to print when they do not parse.</returns>
    public static (CheckArguments? Arguments, string? Refusal) Parse(IReadOnlyList<string> args)
    {
        var files = new List<string>();
        var maxErrors = 0;
        var allErrors = false;
        var json = false;
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
                files.Add(token);
                continue;
            }

            if (IsHelp(token))
                continue;
            if (token == "--json")
            {
                json = true;
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

            if (token.StartsWith("--", StringComparison.Ordinal))
                return (null, $"fuse: check takes files and [--max-errors <n>|--all-errors] [--json]; '{token}' is not one");
            // A single dash that is not help starts a file, such as -h.cs.
            files.Add(token);
        }

        if (allErrors && maxErrors > 0)
            return (null, "fuse: --all-errors and --max-errors cannot be combined; choose one");
        return (new CheckArguments(files, allErrors ? int.MaxValue : maxErrors, json), null);
    }

    /// <summary>Whether <paramref name="token"/> asks for help, which the command answers before parsing.</summary>
    private static bool IsHelp(string token) => token is "--help" or "-h";

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
