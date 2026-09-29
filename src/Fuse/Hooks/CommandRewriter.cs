using System.Text.RegularExpressions;

namespace Fuse.Hooks;

/// <summary>
///     Rewrites <c>dotnet build</c> and <c>dotnet test</c> in a shell command to <c>fuse build</c> and <c>fuse test</c>,
///     without parsing shell quoting: after every command separator (<see cref="Rewrite"/>), or only in a command that is
///     one plain invocation (<see cref="RewriteWhole"/>).
/// </summary>
internal static partial class CommandRewriter
{
    /// <summary>Returns the rewritten command, or null when it contains no <c>dotnet build</c> or <c>dotnet test</c> at the start of a command segment.</summary>
    public static string? Rewrite(string command)
    {
        var rewritten = Segment().Replace(command, m => m.Groups["lead"].Value + "fuse " + m.Groups["verb"].Value);
        return rewritten == command ? null : rewritten;
    }

    /// <summary>
    ///     Returns the rewritten command when the whole command is one <c>dotnet build</c> or <c>dotnet test</c> with plain
    ///     arguments, and null otherwise. A plain argument holds only letters, digits and <c>- _ . / \ : = , + ~</c>, so the
    ///     command has no separator, pipe, redirection, substitution, quote, backtick, <c>$</c>, glob or line break, and a
    ///     shell runs nothing but that one invocation. This is the only rewrite a harness that approves the command it is
    ///     given gets, so the approval covers nothing else the agent wrote.
    /// </summary>
    public static string? RewriteWhole(string command) => WholeInvocation().IsMatch(command) ? Rewrite(command) : null;

    // A command segment starts at the beginning, or after &&, ||, ;, | or a newline, optionally followed by whitespace.
    [GeneratedRegex(@"(?<lead>(?:^|&&|\|\||[;|\n])\s*)dotnet(?:\.exe)?\s+(?<verb>build|test)(?=[\s;&|)]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex Segment();

    // \A and \z, not ^ and $: $ also matches before a final line break, which a shell reads as the end of a command.
    [GeneratedRegex(@"\A[ \t]*dotnet(?:\.exe)?[ \t]+(?:build|test)(?:[ \t]+[\w\-./\\:=,+~]+)*[ \t]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex WholeInvocation();
}
