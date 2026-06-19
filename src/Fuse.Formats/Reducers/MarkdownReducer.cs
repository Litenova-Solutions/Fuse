using Fuse.Languages.Abstractions.Reducers;
using Fuse.Languages.Abstractions.Options;
using System.Text.RegularExpressions;

namespace Fuse.Formats.Reducers;

/// <summary>
///     Reduces Markdown files by removing decorative noise while preserving structure.
/// </summary>
public sealed partial class MarkdownReducer : IContentReducer
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".md"];

    /// <inheritdoc />
    public string Reduce(string content, ReductionOptions options)
    {
        content = HtmlCommentRegex().Replace(content, string.Empty);
        content = UnderlineHeadingRegex().Replace(content, m =>
        {
            var text = m.Groups["text"].Value.Trim();
            var underline = m.Groups["underline"].Value;
            var level = underline.StartsWith('=') ? "#" : "##";
            return $"{level} {text}";
        });
        content = HorizontalRuleRegex().Replace(content, string.Empty);
        content = TablePipeSpaceRegex().Replace(content, "|");
        content = LinkTitleRegex().Replace(content, "[$1]($2)");
        content = ExcessiveNewlinesRegex().Replace(content, "\n\n");
        content = TrailingLineWhitespaceRegex().Replace(content, string.Empty);
        content = LeadingNewlinesRegex().Replace(content, string.Empty);
        content = TrailingNewlinesRegex().Replace(content, string.Empty);
        return content.Trim();
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex HtmlCommentRegex();

    [GeneratedRegex(@"(?m)^(?<text>.+)(\r?\n)(?<underline>[=\-]{3,})\s*$", RegexOptions.Compiled)]
    private static partial Regex UnderlineHeadingRegex();

    [GeneratedRegex(@"(?m)^\s*[-*_]{3,}\s*(\r?\n)?", RegexOptions.Compiled)]
    private static partial Regex HorizontalRuleRegex();

    [GeneratedRegex(@"\s*\|\s*", RegexOptions.Compiled)]
    private static partial Regex TablePipeSpaceRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^\s)]+)\s+""[^""]*""\)", RegexOptions.Compiled)]
    private static partial Regex LinkTitleRegex();

    [GeneratedRegex(@"(\r?\n){3,}", RegexOptions.Compiled)]
    private static partial Regex ExcessiveNewlinesRegex();

    [GeneratedRegex(@"(?<!  )[ \t]+$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex TrailingLineWhitespaceRegex();

    [GeneratedRegex(@"^[\r\n]+", RegexOptions.Compiled)]
    private static partial Regex LeadingNewlinesRegex();

    [GeneratedRegex(@"[\r\n]+$", RegexOptions.Compiled)]
    private static partial Regex TrailingNewlinesRegex();
}
