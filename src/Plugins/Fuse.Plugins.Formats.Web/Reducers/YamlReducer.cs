using Fuse.Plugins.Abstractions.Reducers;
using Fuse.Plugins.Abstractions.Options;
using System.Text.RegularExpressions;

namespace Fuse.Plugins.Formats.Web.Reducers;

/// <summary>
///     Reduces YAML files by removing comments and excessive blank lines.
/// </summary>
public sealed partial class YamlReducer : IContentReducer
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".yaml", ".yml"];

    /// <inheritdoc />
    public string Reduce(string content, ReductionOptions options)
    {
        content = CommentLineRegex().Replace(content, string.Empty);
        content = TrailingWhitespaceRegex().Replace(content, string.Empty);
        content = ExcessiveNewlinesRegex().Replace(content, "\n\n");
        content = LeadingNewlinesRegex().Replace(content, string.Empty);
        content = TrailingNewlinesRegex().Replace(content, string.Empty);
        return content.Trim();
    }

    [GeneratedRegex(@"^\s*#.*$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex CommentLineRegex();

    [GeneratedRegex(@"[ \t]+$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex TrailingWhitespaceRegex();

    [GeneratedRegex(@"(\r?\n){3,}", RegexOptions.Compiled)]
    private static partial Regex ExcessiveNewlinesRegex();

    [GeneratedRegex(@"^[\r\n]+", RegexOptions.Compiled)]
    private static partial Regex LeadingNewlinesRegex();

    [GeneratedRegex(@"[\r\n]+$", RegexOptions.Compiled)]
    private static partial Regex TrailingNewlinesRegex();
}
