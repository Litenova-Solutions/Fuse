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
    ///     arguments, after plain variable assignments if any, and null otherwise. A plain argument holds only letters, digits and <c>- _ . / \ : = , + ~</c>, so the
    ///     command has no separator, pipe, redirection, substitution, quote, backtick, <c>$</c>, glob or line break, and a
    ///     shell runs nothing but that one invocation. This is the only rewrite a harness that approves the command it is
    ///     given gets, so the approval covers nothing else the agent wrote.
    /// </summary>
    public static string? RewriteWhole(string command) => WholeInvocation().IsMatch(command) ? Rewrite(command) : null;

    // A command segment starts at the beginning, or after &&, ||, ;, | or a newline, optionally followed by whitespace and
    // by variable assignments such as DOTNET_CLI_UI_LANGUAGE=en: a name, =, and a value with no whitespace, quote,
    // backtick, separator, redirection or parenthesis. The assignments are part of the lead, so they are kept as written.
    [GeneratedRegex(@"(?<lead>(?:^|&&|\|\||[;|\n])\s*(?:[A-Za-z_][A-Za-z0-9_]*=[^\s'""`;&|<>()]*\s+)*)dotnet(?:\.exe)?\s+(?<verb>build|test)(?=[\s;&|)]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex Segment();

    // \A and \z, not ^ and $: $ also matches before a final line break, which a shell reads as the end of a command.
    // Leading assignments take plain values only, the characters of a plain argument.
    [GeneratedRegex(@"\A[ \t]*(?:[A-Za-z_][A-Za-z0-9_]*=[\w\-./\\:=,+~]*[ \t]+)*dotnet(?:\.exe)?[ \t]+(?:build|test)(?:[ \t]+[\w\-./\\:=,+~]+)*[ \t]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex WholeInvocation();
}
