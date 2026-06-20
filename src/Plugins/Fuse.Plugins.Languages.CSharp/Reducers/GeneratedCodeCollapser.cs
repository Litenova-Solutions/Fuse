using System.Text.RegularExpressions;
using Fuse.Plugins.Languages.CSharp.Dependencies;

namespace Fuse.Plugins.Languages.CSharp.Reducers;

/// <summary>
///     Collapses the large machine-generated method bodies in EF Core migrations and model snapshots, keeping
///     the class and method signatures but replacing the bodies with a short placeholder.
/// </summary>
/// <remarks>
///     Fuse already excludes most generated C# by default (the <c>*.g.cs</c>, <c>*.Designer.cs</c>, and
///     <c>*.generated.cs</c> patterns and the auto-generated header filter). EF migrations and the model
///     snapshot are the common generated files those rules miss: they carry hand-authored class names but
///     enormous generated <c>Up</c>, <c>Down</c>, <c>BuildModel</c>, and <c>BuildTargetModel</c> bodies. This
///     collapser keeps what an agent needs (which migration, which methods) and drops the bodies.
///     <para>
///         Brace and parenthesis matching runs over a sanitized copy (comments and string literals blanked),
///         so braces inside strings do not skew the match; replacements are applied to the original text.
///     </para>
/// </remarks>
public static partial class GeneratedCodeCollapser
{
    private const string Placeholder = " /* fuse: collapsed generated body */ ";

    // The generated methods whose bodies are collapsed when a file is recognized as EF-generated.
    private static readonly string[] CollapsibleMethods = ["Up", "Down", "BuildModel", "BuildTargetModel"];

    /// <summary>
    ///     Returns whether the content looks like an EF Core migration or model snapshot.
    /// </summary>
    /// <param name="content">The C# source to inspect.</param>
    /// <returns><see langword="true" /> when the file appears to be EF-generated.</returns>
    public static bool IsGenerated(string content)
    {
        if (string.IsNullOrEmpty(content))
            return false;

        return content.Contains("Microsoft.EntityFrameworkCore.Migrations", StringComparison.Ordinal)
            || content.Contains(": Migration", StringComparison.Ordinal)
            || content.Contains("ModelSnapshot", StringComparison.Ordinal)
            || content.Contains("MigrationBuilder", StringComparison.Ordinal)
            || content.Contains("BuildTargetModel", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Collapses generated method bodies when the content is recognized as EF-generated; otherwise returns
    ///     the content unchanged.
    /// </summary>
    /// <param name="content">The C# source to collapse.</param>
    /// <returns>The collapsed content, or the original when it is not recognized as generated.</returns>
    public static string Collapse(string content)
    {
        if (!IsGenerated(content))
            return content;

        var sanitized = CSharpSourceSanitizer.Sanitize(content);

        // Collect body spans first, then apply replacements from last to first so earlier indices stay valid.
        var spans = new List<(int Open, int Close)>();
        foreach (Match match in MethodRegex().Matches(sanitized))
        {
            var parenOpen = match.Index + match.Length - 1;
            var parenClose = MatchDelimiter(sanitized, parenOpen, '(', ')');
            if (parenClose < 0)
                continue;

            var braceOpen = NextBlockBrace(sanitized, parenClose + 1);
            if (braceOpen < 0)
                continue;

            var braceClose = MatchDelimiter(sanitized, braceOpen, '{', '}');
            if (braceClose < 0)
                continue;

            spans.Add((braceOpen, braceClose));
        }

        if (spans.Count == 0)
            return content;

        var result = content;
        foreach (var (open, close) in spans.OrderByDescending(s => s.Open))
            result = result[..(open + 1)] + Placeholder + result[close..];

        return result;
    }

    // Finds the first '{' that opens a block body after a method's parameter list, returning -1 when an
    // expression body (=>) or a semicolon (abstract or interface method) is reached first.
    private static int NextBlockBrace(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{')
                return i;
            if (c == ';')
                return -1;
            if (c == '=' && i + 1 < text.Length && text[i + 1] == '>')
                return -1;
        }

        return -1;
    }

    // Returns the index of the delimiter that matches the opening delimiter at openIndex, or -1 if unbalanced.
    private static int MatchDelimiter(string text, int openIndex, char open, char close)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == open)
                depth++;
            else if (text[i] == close && --depth == 0)
                return i;
        }

        return -1;
    }

    [GeneratedRegex(@"\b(?:Up|Down|BuildModel|BuildTargetModel)\s*\(", RegexOptions.Compiled)]
    private static partial Regex MethodRegex();
}
