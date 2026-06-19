using Fuse.Languages.Abstractions.Reducers;
using Fuse.Languages.Abstractions.Options;
using System.Text.RegularExpressions;

namespace Fuse.Formats.Reducers;

/// <summary>
///     Reduces CSS files by removing comments and unnecessary whitespace.
/// </summary>
public sealed partial class CssReducer : IContentReducer
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".css"];

    /// <inheritdoc />
    public string Reduce(string content, ReductionOptions options)
    {
        content = BlockCommentRegex().Replace(content, string.Empty);
        content = NewlineRegex().Replace(content, string.Empty);
        content = OpenBraceSpaceRegex().Replace(content, "{");
        content = CloseBraceSpaceRegex().Replace(content, "}");
        content = ColonSpaceRegex().Replace(content, ":");
        content = SemicolonSpaceRegex().Replace(content, ";");
        content = CommaSpaceRegex().Replace(content, ",");
        content = MultiSpaceRegex().Replace(content, " ");
        return content.Trim();
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex BlockCommentRegex();

    [GeneratedRegex(@"[\r\n]+", RegexOptions.Compiled)]
    private static partial Regex NewlineRegex();

    [GeneratedRegex(@"\s*\{\s*", RegexOptions.Compiled)]
    private static partial Regex OpenBraceSpaceRegex();

    [GeneratedRegex(@"\s*\}\s*", RegexOptions.Compiled)]
    private static partial Regex CloseBraceSpaceRegex();

    [GeneratedRegex(@"\s*:\s*", RegexOptions.Compiled)]
    private static partial Regex ColonSpaceRegex();

    [GeneratedRegex(@"\s*;\s*", RegexOptions.Compiled)]
    private static partial Regex SemicolonSpaceRegex();

    [GeneratedRegex(@"\s*,\s*", RegexOptions.Compiled)]
    private static partial Regex CommaSpaceRegex();

    [GeneratedRegex(@" {2,}", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();
}
