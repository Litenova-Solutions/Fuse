using System.Text;
using System.Text.Json;
using Fuse.Emission.Models;
using Fuse.Emission.Serialization;
using Fuse.Plugins.Abstractions.Outline;

namespace Fuse.Emission.Manifest;

/// <summary>
///     One file in a table of contents: its path, the token cost of reading it under the current reduction,
///     and its structural outline.
/// </summary>
/// <param name="Path">The normalized, forward-slash relative path of the file.</param>
/// <param name="Tokens">The token cost of reading the file's reduced content.</param>
/// <param name="Symbols">The declared types and members, or an empty list when no outline is available.</param>
public sealed record TocFileEntry(string Path, long Tokens, IReadOnlyList<OutlineSymbol> Symbols);

/// <summary>
///     Builds the table-of-contents document: a directory tree annotated with per-file token costs and a
///     symbol outline. The document is a cheap first call that lets an agent decide which files to read in full
///     before spending the tokens to fetch them.
/// </summary>
public static class TableOfContentsBuilder
{
    /// <summary>
    ///     Renders a table of contents for the supplied files in the requested output format.
    /// </summary>
    /// <param name="files">The files to list, each with its token cost and outline.</param>
    /// <param name="format">The output format that selects the document representation.</param>
    /// <returns>
    ///     The rendered table-of-contents document terminated by a newline, or <see cref="string.Empty" />
    ///     when <paramref name="files" /> is empty.
    /// </returns>
    /// <remarks>
    ///     Files are listed sorted by path. <see cref="OutputFormat.Json" /> produces a structured object;
    ///     all other formats produce an indented directory tree with a header line.
    /// </remarks>
    public static string Build(IReadOnlyList<TocFileEntry> files, OutputFormat format)
    {
        if (files.Count == 0)
            return string.Empty;

        return format == OutputFormat.Json
            ? BuildJson(files)
            : BuildTree(files);
    }

    private static string BuildJson(IReadOnlyList<TocFileEntry> files)
    {
        var dto = new JsonTocDto
        {
            Files = files.Count,
            ReadCostTokens = files.Sum(f => f.Tokens),
            Entries = files
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Select(f => new JsonTocFileDto
                {
                    Path = f.Path,
                    Tokens = f.Tokens,
                    Symbols = f.Symbols
                        .Select(s => new JsonTocSymbolDto
                        {
                            Kind = s.Kind,
                            Name = s.Name,
                            Members = s.Members.ToArray(),
                        })
                        .ToArray(),
                })
                .ToArray(),
        };

        return JsonSerializer.Serialize(dto, FuseEmissionJsonContext.Default.JsonTocDto) + "\n";
    }

    private static string BuildTree(IReadOnlyList<TocFileEntry> files)
    {
        var ordered = files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var totalTokens = ordered.Sum(f => f.Tokens);

        var sb = new StringBuilder();
        sb.Append("<!-- fuse:table-of-contents files=").Append(ordered.Count)
            .Append(" read-cost=~").Append(FormatTokens(totalTokens)).Append(" tokens\n");
        sb.Append("     A map of the codebase. Each file shows its token cost to read and the types it declares.\n");
        sb.Append("     Fetch a file's full content with fuse_focus, fuse_search, or by path. -->\n");

        // Render an indented directory tree. Directories that contain only a single child collapse visually by
        // still printing each path segment, which keeps the tree unambiguous and cheap to scan.
        string[] previousDirs = [];
        foreach (var file in ordered)
        {
            var slash = file.Path.LastIndexOf('/');
            var dirs = slash < 0 ? [] : file.Path[..slash].Split('/');
            var fileName = slash < 0 ? file.Path : file.Path[(slash + 1)..];

            var common = CommonPrefixLength(previousDirs, dirs);
            for (var d = common; d < dirs.Length; d++)
                sb.Append(Indent(d)).Append(dirs[d]).Append("/\n");
            previousDirs = dirs;

            sb.Append(Indent(dirs.Length)).Append(fileName)
                .Append(" (~").Append(FormatTokens(file.Tokens)).Append(" tokens)\n");

            foreach (var symbol in file.Symbols)
            {
                sb.Append(Indent(dirs.Length + 1)).Append(symbol.Kind).Append(' ').Append(symbol.Name);
                if (symbol.Members.Count > 0)
                    sb.Append(": ").Append(string.Join(", ", symbol.Members));
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    private static int CommonPrefixLength(string[] a, string[] b)
    {
        var max = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < max && string.Equals(a[i], b[i], StringComparison.Ordinal))
            i++;
        return i;
    }

    private static string Indent(int level) => new(' ', level * 2);

    private static string FormatTokens(long count) =>
        count >= 1000 ? $"{count / 1000.0:F1}k" : count.ToString();
}
