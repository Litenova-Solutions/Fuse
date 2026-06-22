using Fuse.Plugins.Languages.CSharp.Lexing;

namespace Fuse.Plugins.Languages.CSharp.Dependencies;

/// <summary>
///     Blanks out comments and string and character literals in C# source so that downstream regex matching
///     sees only code. Replacement preserves character positions and newlines: each removed character is
///     swapped for a space (or kept, for newlines), so line and column offsets are unchanged.
/// </summary>
/// <remarks>
///     This is a best-effort lexical pass, not a full C# lexer. It recognizes line comments, block comments,
///     character literals, regular and verbatim strings, raw string literals, and interpolated strings via the
///     shared <see cref="CSharpStringScanner" />. Interpolation holes are blanked along with their surrounding
///     literal, so type names appearing only inside <c>$"{...}"</c> expressions are not treated as references.
///     The goal is to remove the dominant source of false-positive dependency edges, namely type names
///     mentioned in comments and strings.
/// </remarks>
public static class CSharpSourceSanitizer
{
    /// <summary>
    ///     Returns a copy of <paramref name="content" /> with comment and literal text replaced by spaces.
    /// </summary>
    /// <param name="content">The C# source to sanitize. A null or empty value is returned unchanged.</param>
    /// <returns>
    ///     Source of identical length with comments and string and character literals blanked to spaces,
    ///     leaving code tokens and structural punctuation intact.
    /// </returns>
    public static string Sanitize(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        var spans = CSharpStringScanner.Scan(content);
        if (spans.Count == 0)
            return content;

        var buffer = content.ToCharArray();
        foreach (var span in spans)
        {
            var end = span.Start + span.Length;
            for (var k = span.Start; k < end; k++)
                Blank(buffer, k);
        }

        return new string(buffer);
    }

    private static void Blank(char[] buffer, int index)
    {
        if (buffer[index] != '\n' && buffer[index] != '\r')
            buffer[index] = ' ';
    }
}
