using Fuse.Plugins.Abstractions.Reducers;
using Fuse.Plugins.Abstractions.Options;
using System.Text.RegularExpressions;

namespace Fuse.Plugins.Formats.Web.Reducers;

/// <summary>
///     Reduces JavaScript and TypeScript files by removing comments and unnecessary whitespace. The same
///     lexical rules apply across the family (including JSX and TSX) because they share comment and whitespace
///     syntax; type annotations are treated as ordinary tokens and preserved.
/// </summary>
public sealed partial class JavaScriptReducer : IContentReducer
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } =
        [".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".mts", ".cts"];

    /// <inheritdoc />
    public string Reduce(string content, ReductionOptions options)
    {
        content = LineCommentRegex().Replace(content, string.Empty);
        content = BlockCommentRegex().Replace(content, string.Empty);
        content = DoubleNewlineRegex().Replace(content, "\n");
        content = TrimLineWhitespaceRegex().Replace(content, string.Empty);
        content = MultiSpaceRegex().Replace(content, " ");
        content = SyntaxWhitespaceRegex().Replace(content, "$1");
        return content.Trim();
    }

    [GeneratedRegex(@"(?<!:)//(?!/)[^\r\n]*", RegexOptions.Compiled)]
    private static partial Regex LineCommentRegex();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex BlockCommentRegex();

    [GeneratedRegex(@"(\r?\n){2,}", RegexOptions.Compiled)]
    private static partial Regex DoubleNewlineRegex();

    [GeneratedRegex(@"^\s+|\s+$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex TrimLineWhitespaceRegex();

    [GeneratedRegex(@" {2,}", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"\s*([{}\[\]();,:])\s*", RegexOptions.Compiled)]
    private static partial Regex SyntaxWhitespaceRegex();
}
