using System.Text;
using System.Text.Json;
using Fuse.Emission.Models;
using Fuse.Emission.Serialization;
using Fuse.Plugins.Abstractions.Outline;

namespace Fuse.Emission.Manifest;

/// <summary>
///     Builds the table-of-contents document: a directory tree annotated with per-file token costs and a
///     symbol outline. The document is a cheap first call that lets an agent decide which files to read in full
///     before spending the tokens to fetch them.
/// </summary>
public static class TableOfContentsBuilder
{
    /// <summary>
    ///     Renders a table of contents for the supplied files in the requested output format and detail level.
    /// </summary>
    /// <param name="files">The files to list, each with its token cost and outline.</param>
    /// <param name="format">The output format that selects the document representation.</param>
    /// <param name="detail">How much per-file and per-symbol detail to render.</param>
    /// <returns>
    ///     The rendered table-of-contents document terminated by a newline, or <see cref="string.Empty" />
    ///     when <paramref name="files" /> is empty.
    /// </returns>
    /// <remarks>
    ///     Files are listed sorted by path. <see cref="OutputFormat.Json" /> produces a structured object;
    ///     all other formats produce an indented directory tree with a header line. Use a lower
    ///     <paramref name="detail" /> level to keep the document within a size budget on a large codebase.
    /// </remarks>
    public static string Build(
        IReadOnlyList<TableOfContentsFileEntry> files,
        OutputFormat format,
        TableOfContentsDetail detail = TableOfContentsDetail.Full)
    {
        if (files.Count == 0)
            return string.Empty;

        return format == OutputFormat.Json
            ? BuildJson(files, detail)
            : BuildTree(files, detail);
    }

    private static string BuildJson(IReadOnlyList<TableOfContentsFileEntry> files, TableOfContentsDetail detail)
    {
        if (detail == TableOfContentsDetail.Directories)
        {
            var dirs = AggregateByDirectory(files);
            var dirDto = new JsonTocDto
            {
                Files = files.Count,
                ReadCostTokens = files.Sum(f => f.Tokens),
                Entries = dirs
                    .Select(d => new JsonTocFileDto { Path = d.Directory, Tokens = d.Tokens, Symbols = [] })
                    .ToArray(),
            };

            return JsonSerializer.Serialize(dirDto, FuseEmissionJsonContext.Default.JsonTocDto) + "\n";
        }

        var includeSymbols = detail == TableOfContentsDetail.Full;
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
                    Symbols = includeSymbols
                        ? f.Symbols
                            .Select(s => new JsonTocSymbolDto
                            {
                                Kind = s.Kind,
                                Name = s.Name,
                                Members = s.Members.ToArray(),
                            })
                            .ToArray()
                        : [],
                })
                .ToArray(),
        };

        return JsonSerializer.Serialize(dto, FuseEmissionJsonContext.Default.JsonTocDto) + "\n";
    }

    private static string BuildTree(IReadOnlyList<TableOfContentsFileEntry> files, TableOfContentsDetail detail)
    {
        var ordered = files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var totalTokens = ordered.Sum(f => f.Tokens);

        var sb = new StringBuilder();
        sb.Append("<!-- fuse:table-of-contents files=").Append(ordered.Count)
            .Append(" read-cost=~").Append(FormatTokens(totalTokens)).Append(" tokens\n");
        sb.Append("     A map of the codebase. Each file shows its token cost to read and the types it declares.\n");

        // When degraded to fit a size budget, say so plainly so the agent treats the map as a starting point
        // rather than the whole picture, and knows how to drill in.
        switch (detail)
        {
            case TableOfContentsDetail.PathsOnly:
                sb.Append("     Type outlines omitted to fit the size budget; call fuse_skeleton for signatures.\n");
                break;
            case TableOfContentsDetail.Directories:
                sb.Append("     Large codebase: showing directories and aggregate token costs only. Call fuse_toc\n");
                sb.Append("     on a subdirectory, or fuse_search/fuse_focus, to drill into files.\n");
                break;
        }

        sb.Append("     Fetch a file's full content with fuse_focus, fuse_search, or by path. -->\n");

        if (detail == TableOfContentsDetail.Directories)
        {
            foreach (var dir in AggregateByDirectory(ordered))
            {
                sb.Append(dir.Directory).Append(" (").Append(dir.FileCount).Append(" files, ~")
                    .Append(FormatTokens(dir.Tokens)).Append(" tokens)\n");
            }

            return sb.ToString();
        }

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

            if (detail != TableOfContentsDetail.Full)
                continue;

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

    // Groups files by their immediate parent directory (root files under "."), preserving sort order, so the
    // Directories detail level can show one aggregate row per directory instead of every file.
    private static IReadOnlyList<TableOfContentsDirectoryAggregate> AggregateByDirectory(
        IReadOnlyList<TableOfContentsFileEntry> files)
    {
        return files
            .GroupBy(f =>
            {
                var slash = f.Path.LastIndexOf('/');
                return slash < 0 ? "." : f.Path[..slash];
            })
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TableOfContentsDirectoryAggregate(g.Key + "/", g.Count(), g.Sum(f => f.Tokens)))
            .ToList();
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
