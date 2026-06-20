using Fuse.Plugins.Abstractions.Reducers;
using Fuse.Plugins.Abstractions.Options;
using System.Text.RegularExpressions;

namespace Fuse.Plugins.Formats.Web.Reducers;

/// <summary>
///     Reduces XML files, including project and MSBuild files, by removing comments and whitespace.
/// </summary>
public sealed partial class XmlReducer : IContentReducer
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".xml", ".csproj", ".targets", ".props"];

    /// <inheritdoc />
    public string Reduce(string content, ReductionOptions options)
    {
        content = XmlCommentRegex().Replace(content, string.Empty);
        content = TagWhitespaceRegex().Replace(content, "><");
        content = LeadingWhitespaceRegex().Replace(content, string.Empty);
        content = TrailingWhitespaceRegex().Replace(content, string.Empty);
        content = XmlDeclarationBreakRegex().Replace(content, "?>\n");
        content = InteriorNewlineRegex().Replace(content, string.Empty);
        content = MultiSpaceRegex().Replace(content, " ");
        return content.Trim();
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex XmlCommentRegex();

    [GeneratedRegex(@">\s+<", RegexOptions.Compiled)]
    private static partial Regex TagWhitespaceRegex();

    [GeneratedRegex(@"^\s+", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex LeadingWhitespaceRegex();

    [GeneratedRegex(@"\s+$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex TrailingWhitespaceRegex();

    [GeneratedRegex(@"(\?>\s*)", RegexOptions.Compiled)]
    private static partial Regex XmlDeclarationBreakRegex();

    [GeneratedRegex(@"[\r\n]+(?!<\?)", RegexOptions.Compiled)]
    private static partial Regex InteriorNewlineRegex();

    [GeneratedRegex(@" {2,}", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();
}
