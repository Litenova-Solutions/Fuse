namespace Fuse.Plugins.Languages.CSharp.Lexing;

/// <summary>
///     Classifies the lexical spans produced by <see cref="CSharpStringScanner" />.
/// </summary>
internal enum CSharpSpanKind
{
    /// <summary>A <c>// ...</c> line comment, including the leading slashes.</summary>
    LineComment,

    /// <summary>A <c>/* ... */</c> block comment, including both delimiters.</summary>
    BlockComment,

    /// <summary>
    ///     A string literal of any form: regular, verbatim (<c>@"..."</c>), interpolated (<c>$"..."</c>),
    ///     or raw (<c>"""..."""</c>), including its opening and closing delimiters.
    /// </summary>
    String,

    /// <summary>A character literal (<c>'x'</c>), including the surrounding quotes.</summary>
    CharLiteral,
}

/// <summary>
///     A half-open <c>[Start, Start+Length)</c> range over the source identifying a comment or literal.
/// </summary>
/// <param name="Start">Zero-based index of the first character of the span.</param>
/// <param name="Length">Number of characters the span covers.</param>
/// <param name="Kind">What the span lexically is.</param>
internal readonly record struct CSharpLexicalSpan(int Start, int Length, CSharpSpanKind Kind);

/// <summary>
///     Single best-effort lexical pass that locates C# comments, string literals (regular, verbatim,
///     interpolated, and raw), and character literals. Shared by <see cref="Dependencies.CSharpSourceSanitizer" />
///     (which blanks the spans) and the reducer (which masks literal spans behind placeholders), so the two
///     never diverge on where a literal starts and ends.
/// </summary>
/// <remarks>
///     This is not a full C# lexer. Interpolation holes are treated as part of their enclosing literal rather
///     than recursed into, matching the historical sanitizer behavior. Unterminated non-verbatim strings stop
///     at the end of the line.
/// </remarks>
internal static class CSharpStringScanner
{
    /// <summary>
    ///     Scans <paramref name="content" /> and returns the comment and literal spans in source order, with
    ///     no overlaps (an inner literal inside a comment is subsumed by the comment span).
    /// </summary>
    /// <param name="content">The C# source to scan. Null or empty yields an empty list.</param>
    /// <returns>The non-overlapping spans in ascending start order.</returns>
    public static IReadOnlyList<CSharpLexicalSpan> Scan(string content)
    {
        var spans = new List<CSharpLexicalSpan>();
        if (string.IsNullOrEmpty(content))
            return spans;

        var length = content.Length;
        var i = 0;

        while (i < length)
        {
            var c = content[i];

            if (c == '/' && i + 1 < length && content[i + 1] == '/')
            {
                var start = i;
                while (i < length && content[i] != '\n')
                    i++;
                spans.Add(new CSharpLexicalSpan(start, i - start, CSharpSpanKind.LineComment));
                continue;
            }

            if (c == '/' && i + 1 < length && content[i + 1] == '*')
            {
                var start = i;
                i += 2;
                while (i < length)
                {
                    if (content[i] == '*' && i + 1 < length && content[i + 1] == '/')
                    {
                        i += 2;
                        break;
                    }

                    i++;
                }

                spans.Add(new CSharpLexicalSpan(start, i - start, CSharpSpanKind.BlockComment));
                continue;
            }

            var rawQuotes = CountRawStringQuotes(content, i);
            if (rawQuotes >= 3)
            {
                var start = i;
                i = ScanRawString(content, i, rawQuotes);
                spans.Add(new CSharpLexicalSpan(start, i - start, CSharpSpanKind.String));
                continue;
            }

            if (c == '"' || IsInterpolatedOrVerbatimStart(content, i))
            {
                var start = i;
                i = ScanString(content, i);
                spans.Add(new CSharpLexicalSpan(start, i - start, CSharpSpanKind.String));
                continue;
            }

            if (c == '\'')
            {
                var start = i;
                i = ScanCharLiteral(content, i);
                spans.Add(new CSharpLexicalSpan(start, i - start, CSharpSpanKind.CharLiteral));
                continue;
            }

            i++;
        }

        return spans;
    }

    private static bool IsInterpolatedOrVerbatimStart(string content, int i)
    {
        // Matches the prefix of @"...", $"...", $@"...", and @$"..." so the scanner enters string mode.
        var c = content[i];
        if (c != '@' && c != '$')
            return false;

        var j = i + 1;
        if (j < content.Length && (content[j] == '@' || content[j] == '$'))
            j++;

        return j < content.Length && content[j] == '"';
    }

    private static int ScanString(string content, int i)
    {
        // Skip any @/$ prefix characters, tracking whether the literal is verbatim.
        var verbatim = false;
        while (i < content.Length && (content[i] == '@' || content[i] == '$'))
        {
            if (content[i] == '@')
                verbatim = true;
            i++;
        }

        if (i >= content.Length || content[i] != '"')
            return i;

        i++; // opening quote

        while (i < content.Length)
        {
            var c = content[i];

            if (verbatim)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }

                    i++;
                    break;
                }

                i++;
                continue;
            }

            if (c == '\\' && i + 1 < content.Length)
            {
                i += 2;
                continue;
            }

            if (c == '"')
            {
                i++;
                break;
            }

            if (c == '\n')
                break; // unterminated non-verbatim string; stop at line end

            i++;
        }

        return i;
    }

    private static int ScanCharLiteral(string content, int i)
    {
        i++; // opening quote

        while (i < content.Length)
        {
            var c = content[i];

            if (c == '\\' && i + 1 < content.Length)
            {
                i += 2;
                continue;
            }

            if (c == '\'')
            {
                i++;
                break;
            }

            if (c == '\n')
                break;

            i++;
        }

        return i;
    }

    /// <summary>
    ///     Counts the run of <c>"</c> characters opening a candidate raw string literal at
    ///     <paramref name="i" />, skipping any leading <c>$</c> interpolation prefixes. A count of three or
    ///     more denotes a raw string literal.
    /// </summary>
    public static int CountRawStringQuotes(string content, int i)
    {
        // Optional $ prefixes may precede a raw string literal opener.
        var j = i;
        while (j < content.Length && content[j] == '$')
            j++;

        var count = 0;
        while (j < content.Length && content[j] == '"')
        {
            count++;
            j++;
        }

        return count;
    }

    private static int ScanRawString(string content, int i, int quoteCount)
    {
        while (i < content.Length && content[i] == '$')
            i++;

        i += quoteCount;

        // The literal ends at the first run of quoteCount (or more) consecutive quotes.
        while (i < content.Length)
        {
            if (content[i] == '"')
            {
                var run = 0;
                while (i + run < content.Length && content[i + run] == '"')
                    run++;

                i += run;
                if (run >= quoteCount)
                    break;

                continue;
            }

            i++;
        }

        return i;
    }
}
