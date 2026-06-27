using System.Text;
using System.Text.RegularExpressions;

namespace Fuse.Indexing;

/// <summary>
///     Extracts the human-written comment text from a source chunk so it can be indexed as its own weighted
///     full-text field. Comment prose uses the developer's vocabulary, which is the vocabulary a vague query is
///     written in, so it is a strong bridge from a natural-language query to the code it describes.
/// </summary>
/// <remarks>
///     Recognizes the C-family line comment (<c>//</c>, including the XML doc form <c>///</c>) and block comment
///     (<c>/* ... */</c>), and the hash line comment (<c>#</c>) so the extractor carries to more than one
///     language. Comment markers and XML doc tags are stripped, leaving the prose. This is a lexical scan, not a
///     parser, so it is deterministic and language-agnostic by construction; a string literal that looks like a
///     comment is a rare and harmless false positive for a search-bridge field.
/// </remarks>
public static partial class CommentExtractor
{
    /// <summary>
    ///     Extracts and concatenates the comment prose from source content.
    /// </summary>
    /// <param name="content">The raw source content of a chunk.</param>
    /// <returns>The comment prose with markers and tags removed, or an empty string when there are no comments.</returns>
    public static string Extract(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        var builder = new StringBuilder();
        foreach (Match match in CommentPattern().Matches(content))
        {
            var text = match.Value;
            text = CommentMarkers().Replace(text, " ");
            text = XmlDocTags().Replace(text, " ");
            var trimmed = Whitespace().Replace(text, " ").Trim();
            if (trimmed.Length > 0)
            {
                if (builder.Length > 0)
                    builder.Append(' ');
                builder.Append(trimmed);
            }
        }

        return builder.ToString();
    }

    // A block comment, a run of line comments, or a hash comment. Ordered so the block form is tried first.
    [GeneratedRegex(@"/\*[\s\S]*?\*/|(?://[^\n]*\n?)+|(?:#[^\n]*\n?)+", RegexOptions.Compiled)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"/\*+|\*+/|^\s*\*|///?|#", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex CommentMarkers();

    // Strip XML doc tags such as <summary>, </param>, and <see cref="..."/>, leaving any prose between them.
    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex XmlDocTags();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex Whitespace();
}
