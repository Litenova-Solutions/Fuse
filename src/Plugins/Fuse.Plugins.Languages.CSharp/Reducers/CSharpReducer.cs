using Fuse.Plugins.Abstractions.Reducers;
using Fuse.Plugins.Abstractions.Options;
using System.Text.RegularExpressions;

namespace Fuse.Plugins.Languages.CSharp.Reducers;

/// <summary>
///     Reduces <c>.cs</c> source files by stripping comments, preprocessor directives, <c>using</c>
///     statements, and namespace wrappers, with an optional aggressive mode that compresses syntax further.
/// </summary>
/// <remarks>
///     Which steps run is controlled by <see cref="ReductionOptions" /> (for example
///     <see cref="ReductionOptions.RemoveCSharpComments" /> and
///     <see cref="ReductionOptions.AggressiveCSharpReduction" />). Aggressive mode collapses whitespace and
///     rewrites auto-properties, which maximizes token savings but produces output that is no longer
///     guaranteed to compile; comment and string literals are preserved through placeholder substitution so
///     their contents are never altered.
/// </remarks>
public sealed partial class CSharpReducer : IContentReducer
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".cs"];

    /// <inheritdoc />
    public string Reduce(string content, ReductionOptions options)
    {
        content = RemoveComments(content, options);
        content = RemovePreprocessorDirectives(content, options);
        content = RemoveUsings(content, options);
        content = RemoveNamespaces(content, options);

        if (options.AggressiveCSharpReduction)
        {
            content = ApplyAggressiveOptimization(content);
            content = CompressSyntax(content);
        }

        return content.Trim();
    }

    private static string RemoveComments(string content, ReductionOptions options)
    {
        if (!options.RemoveCSharpComments)
            return content;

        return CommentRegex().Replace(content, m =>
        {
            if (m.Groups[1].Success || m.Groups[2].Success || m.Groups[3].Success ||
                m.Groups[4].Success || m.Groups[5].Success)
                return m.Value;

            return string.Empty;
        });
    }

    private static string RemovePreprocessorDirectives(string content, ReductionOptions options)
    {
        if (options.RemoveCSharpRegions && !options.AggressiveCSharpReduction)
            return RegionDirectiveRegex().Replace(content, string.Empty);

        if (options.AggressiveCSharpReduction)
            return AllDirectiveRegex().Replace(content, string.Empty);

        return content;
    }

    private static string RemoveUsings(string content, ReductionOptions options)
    {
        if (!options.RemoveCSharpUsings)
            return content;

        content = UsingStatementRegex().Replace(content, string.Empty);
        content = UsingAliasRegex().Replace(content, string.Empty);
        return content;
    }

    private static string RemoveNamespaces(string content, ReductionOptions options)
    {
        if (!options.RemoveCSharpNamespaces)
            return content;

        content = FileScopedNamespaceRegex().Replace(content, string.Empty);
        content = BlockNamespaceRegex().Replace(content, string.Empty);
        content = NamespaceIndentRegex().Replace(content, string.Empty);
        return content;
    }

    private static string ApplyAggressiveOptimization(string content)
    {
        content = NoiseAttributeRegex().Replace(content, string.Empty);
        content = AssemblySuppressMessageRegex().Replace(content, string.Empty);
        content = ThisKeywordRegex().Replace(content, string.Empty);
        content = AutoPropertyGetSetRegex().Replace(content, "{get;set;}");
        content = AutoPropertyGetRegex().Replace(content, "{get;}");
        content = AutoPropertySetRegex().Replace(content, "{set;}");
        return content;
    }

    private static string CompressSyntax(string content)
    {
        var literals = new List<string>();

        content = StringLiteralRegex().Replace(content, m =>
        {
            literals.Add(m.Value);
            return $"__FUSE_STR_{literals.Count - 1}__";
        });

        content = SyntaxWhitespaceRegex().Replace(content, "$1");
        content = CollapseWhitespaceRegex().Replace(content, " ");

        content = FuseStringPlaceholderRegex().Replace(content, m =>
        {
            if (int.TryParse(m.Groups[1].Value, out var index) && index >= 0 && index < literals.Count)
                return literals[index];

            return m.Value;
        });

        return content;
    }

    [GeneratedRegex(
        @"\[\s*(DebuggerDisplay|DebuggerStepThrough|DebuggerNonUserCode|MethodImpl|EditorBrowsable|Serializable|Obsolete|GeneratedCode|CompilerGenerated|ExcludeFromCodeCoverage|SuppressMessage|AssemblyVersion|AssemblyFileVersion|AssemblyTitle|AssemblyDescription|AssemblyConfiguration|AssemblyCompany|AssemblyProduct|AssemblyCopyright|AssemblyTrademark|AssemblyCulture)(\(.*\))?\s*\]\s*",
        RegexOptions.Compiled)]
    private static partial Regex NoiseAttributeRegex();

    [GeneratedRegex(
        @"(\$@""[^""]*(?:""""[^""]*)*"")|(@\$""[^""]*(?:""""[^""]*)*"")|(@""[^""]*(?:""""[^""]*)*"")|(\$""[^""\\]*(?:\\.[^""\\]*)*"")|(""[^""\\]*(?:\\.[^""\\]*)*"")|(//[^\r\n]*)|(/\*[\s\S]*?\*/)",
        RegexOptions.Compiled)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"^\s*#(region|endregion)[^\r\n]*", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex RegionDirectiveRegex();

    [GeneratedRegex(@"^\s*#.*", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex AllDirectiveRegex();

    [GeneratedRegex(@"^\s*using\s+[\w\.]+;\s*(\r?\n)?", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex UsingStatementRegex();

    [GeneratedRegex(@"^\s*using\s+[A-Za-z0-9_]+\s*=\s*[\w\.]+;\s*(\r?\n)?", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex UsingAliasRegex();

    [GeneratedRegex(@"^\s*namespace\s+[\w\.]+\s*;\s*(\r?\n)?", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex FileScopedNamespaceRegex();

    [GeneratedRegex(@"^\s*namespace\s+[\w\.]+\s*[\r\n\s]*\{", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex BlockNamespaceRegex();

    [GeneratedRegex(@"^(\s{4}|\t)", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex NamespaceIndentRegex();

    [GeneratedRegex(@"^\s*\[assembly:\s*SuppressMessage.*\]\s*$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex AssemblySuppressMessageRegex();

    [GeneratedRegex(@"\bthis\.", RegexOptions.Compiled)]
    private static partial Regex ThisKeywordRegex();

    [GeneratedRegex(@"\{\s*get;\s*set;\s*\}", RegexOptions.Compiled)]
    private static partial Regex AutoPropertyGetSetRegex();

    [GeneratedRegex(@"\{\s*get;\s*\}", RegexOptions.Compiled)]
    private static partial Regex AutoPropertyGetRegex();

    [GeneratedRegex(@"\{\s*set;\s*\}", RegexOptions.Compiled)]
    private static partial Regex AutoPropertySetRegex();

    [GeneratedRegex(
        @"(\$@""[^""]*(?:""""[^""]*)*"")|(@\$""[^""]*(?:""""[^""]*)*"")|(@""[^""]*(?:""""[^""]*)*"")|(\$""[^""\\]*(?:\\.[^""\\]*)*"")|(""[^""\\]*(?:\\.[^""\\]*)*"")",
        RegexOptions.Compiled)]
    private static partial Regex StringLiteralRegex();

    [GeneratedRegex(@"\s*([{};,:()=\[\]])\s*", RegexOptions.Compiled)]
    private static partial Regex SyntaxWhitespaceRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex CollapseWhitespaceRegex();

    [GeneratedRegex(@"__FUSE_STR_(\d+)__", RegexOptions.Compiled)]
    private static partial Regex FuseStringPlaceholderRegex();
}
