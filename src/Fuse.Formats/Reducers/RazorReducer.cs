using Fuse.Languages.Abstractions.Reducers;
using Fuse.Languages.Abstractions.Options;
using System.Text.RegularExpressions;

namespace Fuse.Formats.Reducers;

/// <summary>
///     Reduces Razor and Blazor view files by removing comments and optimizing syntax.
/// </summary>
public sealed partial class RazorReducer : IContentReducer
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".razor", ".cshtml"];

    /// <inheritdoc />
    public string Reduce(string content, ReductionOptions options)
    {
        content = HtmlCommentRegex().Replace(content, string.Empty);
        content = BlockCommentRegex().Replace(content, string.Empty);
        content = LineCommentRegex().Replace(content, string.Empty);
        content = RazorCommentRegex().Replace(content, string.Empty);
        content = TagWhitespaceRegex().Replace(content, "><");
        content = RazorParenOpenRegex().Replace(content, "@(");
        content = RazorParenCloseRegex().Replace(content, ")");
        content = MultiSpaceRegex().Replace(content, " ");
        content = TrimLineWhitespaceRegex().Replace(content, string.Empty);
        content = ExcessiveNewlinesRegex().Replace(content, "\n\n");
        return content.Trim();
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex HtmlCommentRegex();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex BlockCommentRegex();

    [GeneratedRegex(@"(?<!:)//(?!/)[^\r\n]*", RegexOptions.Compiled)]
    private static partial Regex LineCommentRegex();

    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex RazorCommentRegex();

    [GeneratedRegex(@">\s+<", RegexOptions.Compiled)]
    private static partial Regex TagWhitespaceRegex();

    [GeneratedRegex(@"@\(\s+", RegexOptions.Compiled)]
    private static partial Regex RazorParenOpenRegex();

    [GeneratedRegex(@"\s+\)", RegexOptions.Compiled)]
    private static partial Regex RazorParenCloseRegex();

    [GeneratedRegex(@" {2,}", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"^\s+|\s+$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex TrimLineWhitespaceRegex();

    [GeneratedRegex(@"(\r?\n){3,}", RegexOptions.Compiled)]
    private static partial Regex ExcessiveNewlinesRegex();
}
