using System.Text.RegularExpressions;

namespace Fuse.Hooks;

/// <summary>Rewrites <c>dotnet build</c> and <c>dotnet test</c> in a shell command to their Fuse equivalents, leaving everything else untouched.</summary>
internal static partial class CommandRewriter
{
    /// <summary>Returns the rewritten command, or null when it contains no <c>dotnet build</c> or <c>dotnet test</c> at the start of a command segment.</summary>
    public static string? Rewrite(string command)
    {
        var rewritten = Segment().Replace(command, m => m.Groups["lead"].Value + "fuse " + m.Groups["verb"].Value);
        return rewritten == command ? null : rewritten;
    }

    // A command segment starts at the beginning, or after &&, ||, ;, | or a newline, optionally followed by whitespace.
    [GeneratedRegex(@"(?<lead>(?:^|&&|\|\||[;|\n])\s*)dotnet(?:\.exe)?\s+(?<verb>build|test)(?=[\s;&|)]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex Segment();
}
