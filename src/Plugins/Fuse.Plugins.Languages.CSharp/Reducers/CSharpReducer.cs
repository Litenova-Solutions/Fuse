using Fuse.Plugins.Abstractions.Reducers;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Plugins.Languages.CSharp.Lexing;
using System.Text;
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
        // Collapse EF migration and model-snapshot bodies first, so the remaining signatures are then reduced
        // like any other code.
        if (options.CollapseGeneratedCode)
            content = GeneratedCodeCollapser.Collapse(content);

        // Mask every string and character literal (regular, verbatim, interpolated, and raw) behind opaque
        // placeholders before any transform runs. Comment removal and aggressive whitespace compression then
        // operate only on code, so literal contents (embedded JSON, SQL, templates) can never be altered or
        // have a `//` inside a raw string mistaken for a comment. Restored verbatim at the end.
        var literals = new List<string>();
        content = MaskLiterals(content, literals);

        content = RemoveComments(content, options);
        content = RemovePreprocessorDirectives(content, options);
        content = RemoveUsings(content, options);
        content = RemoveNamespaces(content, options);

        if (options.AggressiveCSharpReduction)
        {
            content = ApplyAggressiveOptimization(content);
            content = CompressSyntax(content);
        }

        content = RestoreLiterals(content, literals);
        return content.Trim();
    }

    // Replaces each string/char literal span with __FUSE_STR_n__, recording the original text in `literals`.
    // Comment spans are deliberately left in place so comment removal can still strip them.
    private static string MaskLiterals(string content, List<string> literals)
    {
        var spans = CSharpStringScanner.Scan(content);
        if (spans.Count == 0)
            return content;

        var builder = new StringBuilder(content.Length);
        var cursor = 0;
        foreach (var span in spans)
        {
            if (span.Kind != CSharpSpanKind.String && span.Kind != CSharpSpanKind.CharLiteral)
                continue;

            builder.Append(content, cursor, span.Start - cursor);
            builder.Append("__FUSE_STR_").Append(literals.Count).Append("__");
            literals.Add(content.Substring(span.Start, span.Length));
            cursor = span.Start + span.Length;
        }

        builder.Append(content, cursor, content.Length - cursor);
        return builder.ToString();
    }

    private static string RestoreLiterals(string content, List<string> literals)
    {
        if (literals.Count == 0)
            return content;

        return FuseStringPlaceholderRegex().Replace(content, m =>
        {
            if (int.TryParse(m.Groups[1].Value, out var index) && index >= 0 && index < literals.Count)
                return literals[index];

            return m.Value;
        });
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

    // String and character literals are already masked behind placeholders by MaskLiterals, so collapsing
    // whitespace here can never reach into literal contents.
    private static string CompressSyntax(string content)
    {
        content = SyntaxWhitespaceRegex().Replace(content, "$1");
        content = CollapseWhitespaceRegex().Replace(content, " ");
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

    [GeneratedRegex(@"\s*([{};,:()=\[\]])\s*", RegexOptions.Compiled)]
    private static partial Regex SyntaxWhitespaceRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex CollapseWhitespaceRegex();

    [GeneratedRegex(@"__FUSE_STR_(\d+)__", RegexOptions.Compiled)]
    private static partial Regex FuseStringPlaceholderRegex();
}
