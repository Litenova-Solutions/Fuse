using Fuse.Languages.Abstractions.Reducers;
using Fuse.Languages.Abstractions.Options;
using System.Text.RegularExpressions;

namespace Fuse.Formats.Reducers;

/// <summary>
///     Reduces HTML files by removing comments and unnecessary whitespace.
/// </summary>
public sealed partial class HtmlReducer : IContentReducer
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".html", ".htm"];

    /// <inheritdoc />
    public string Reduce(string content, ReductionOptions options)
    {
        content = HtmlCommentRegex().Replace(content, string.Empty);
        content = TagWhitespaceRegex().Replace(content, "><");
        content = QuotedAttributeRegex().Replace(content, m =>
        {
            var attrValue = m.Groups[2].Value;
            if (UnsafeAttributeValueRegex().IsMatch(attrValue))
                return m.Value;

            return $"{m.Groups[1].Value}={attrValue}";
        });
        content = MultiSpaceRegex().Replace(content, " ");
        content = TrimLineWhitespaceRegex().Replace(content, string.Empty);
        return content.Trim();
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex HtmlCommentRegex();

    [GeneratedRegex(@">\s+<", RegexOptions.Compiled)]
    private static partial Regex TagWhitespaceRegex();

    [GeneratedRegex(@"(\S+)=""([^""\s]+)""", RegexOptions.Compiled)]
    private static partial Regex QuotedAttributeRegex();

    [GeneratedRegex(@"[<>&'""]", RegexOptions.Compiled)]
    private static partial Regex UnsafeAttributeValueRegex();

    [GeneratedRegex(@" {2,}", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"^\s+|\s+$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex TrimLineWhitespaceRegex();
}
