namespace Fuse.Plugins.Languages.CSharp.Dependencies;

/// <summary>
///     Blanks out comments and string and character literals in C# source so that downstream regex matching
///     sees only code. Replacement preserves character positions and newlines: each removed character is
///     swapped for a space (or kept, for newlines), so line and column offsets are unchanged.
/// </summary>
/// <remarks>
///     This is a best-effort lexical pass, not a full C# lexer. It recognizes line comments, block comments,
///     character literals, regular and verbatim strings, raw string literals, and interpolated strings.
///     Interpolation holes are blanked along with their surrounding literal, so type names appearing only
///     inside <c>$"{...}"</c> expressions are not treated as references. The goal is to remove the dominant
///     source of false-positive dependency edges, namely type names mentioned in comments and strings.
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

        var buffer = content.ToCharArray();
        var length = buffer.Length;
        var i = 0;

        while (i < length)
        {
            var c = buffer[i];

            if (c == '/' && i + 1 < length && buffer[i + 1] == '/')
            {
                i = BlankLineComment(buffer, i);
                continue;
            }

            if (c == '/' && i + 1 < length && buffer[i + 1] == '*')
            {
                i = BlankBlockComment(buffer, i);
                continue;
            }

            var rawQuotes = CountRawStringQuotes(buffer, i);
            if (rawQuotes >= 3)
            {
                i = BlankRawString(buffer, i, rawQuotes);
                continue;
            }

            if (c == '"' || IsInterpolatedOrVerbatimStart(buffer, i))
            {
                i = BlankString(buffer, i);
                continue;
            }

            if (c == '\'')
            {
                i = BlankCharLiteral(buffer, i);
                continue;
            }

            i++;
        }

        return new string(buffer);
    }

    private static bool IsInterpolatedOrVerbatimStart(char[] buffer, int i)
    {
        // Matches the prefix of @"...", $"...", $@"...", and @$"..." so the scanner enters string mode.
        var c = buffer[i];
        if (c != '@' && c != '$')
            return false;

        var j = i + 1;
        if (j < buffer.Length && (buffer[j] == '@' || buffer[j] == '$'))
            j++;

        return j < buffer.Length && buffer[j] == '"';
    }

    private static int BlankLineComment(char[] buffer, int i)
    {
        while (i < buffer.Length && buffer[i] != '\n')
            Blank(buffer, i++);

        return i;
    }

    private static int BlankBlockComment(char[] buffer, int i)
    {
        Blank(buffer, i++);
        Blank(buffer, i++);
        while (i < buffer.Length)
        {
            if (buffer[i] == '*' && i + 1 < buffer.Length && buffer[i + 1] == '/')
            {
                Blank(buffer, i++);
                Blank(buffer, i++);
                break;
            }

            Blank(buffer, i++);
        }

        return i;
    }

    private static int BlankString(char[] buffer, int i)
    {
        // Skip any @/$ prefix characters, tracking whether the literal is verbatim.
        var verbatim = false;
        while (i < buffer.Length && (buffer[i] == '@' || buffer[i] == '$'))
        {
            if (buffer[i] == '@')
                verbatim = true;
            Blank(buffer, i++);
        }

        if (i >= buffer.Length || buffer[i] != '"')
            return i;

        Blank(buffer, i++); // opening quote

        while (i < buffer.Length)
        {
            var c = buffer[i];

            if (verbatim)
            {
                if (c == '"')
                {
                    if (i + 1 < buffer.Length && buffer[i + 1] == '"')
                    {
                        Blank(buffer, i++);
                        Blank(buffer, i++);
                        continue;
                    }

                    Blank(buffer, i++);
                    break;
                }

                Blank(buffer, i++);
                continue;
            }

            if (c == '\\' && i + 1 < buffer.Length)
            {
                Blank(buffer, i++);
                Blank(buffer, i++);
                continue;
            }

            if (c == '"')
            {
                Blank(buffer, i++);
                break;
            }

            if (c == '\n')
                break; // unterminated non-verbatim string; stop at line end

            Blank(buffer, i++);
        }

        return i;
    }

    private static int BlankCharLiteral(char[] buffer, int i)
    {
        Blank(buffer, i++); // opening quote

        while (i < buffer.Length)
        {
            var c = buffer[i];

            if (c == '\\' && i + 1 < buffer.Length)
            {
                Blank(buffer, i++);
                Blank(buffer, i++);
                continue;
            }

            if (c == '\'')
            {
                Blank(buffer, i++);
                break;
            }

            if (c == '\n')
                break;

            Blank(buffer, i++);
        }

        return i;
    }

    private static int CountRawStringQuotes(char[] buffer, int i)
    {
        // Optional $ prefixes may precede a raw string literal opener.
        var j = i;
        while (j < buffer.Length && buffer[j] == '$')
            j++;

        var count = 0;
        while (j < buffer.Length && buffer[j] == '"')
        {
            count++;
            j++;
        }

        return count;
    }

    private static int BlankRawString(char[] buffer, int i, int quoteCount)
    {
        while (i < buffer.Length && buffer[i] == '$')
            Blank(buffer, i++);

        for (var q = 0; q < quoteCount; q++)
            Blank(buffer, i++);

        // The literal ends at the first run of quoteCount consecutive quotes.
        while (i < buffer.Length)
        {
            if (buffer[i] == '"')
            {
                var run = 0;
                while (i + run < buffer.Length && buffer[i + run] == '"')
                    run++;

                if (run >= quoteCount)
                {
                    for (var q = 0; q < run; q++)
                        Blank(buffer, i++);
                    break;
                }

                for (var q = 0; q < run; q++)
                    Blank(buffer, i++);
                continue;
            }

            Blank(buffer, i++);
        }

        return i;
    }

    private static void Blank(char[] buffer, int index)
    {
        if (buffer[index] != '\n' && buffer[index] != '\r')
            buffer[index] = ' ';
    }
}
