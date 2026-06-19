using System.Text;
using System.Text.Json;
using Fuse.Analysis.Git;
using Fuse.Analysis.Patterns;
using Fuse.Emission.Models;
using Fuse.Emission.Serialization;

namespace Fuse.Emission.Manifest;

/// <summary>
///     Builds the manifest header prepended before fused file entries.
/// </summary>
public static class ManifestBuilder
{
    /// <summary>
    ///     Renders a manifest block listing the emitted files in the requested output format.
    /// </summary>
    /// <param name="emittedFiles">
    ///     The files included in the manifest, each paired with its token count.
    /// </param>
    /// <param name="format">The output format that selects the manifest representation.</param>
    /// <param name="gitStats">
    ///     Optional git statistics. When available, per-file commit counts and last-modified dates are
    ///     included; when present but unavailable, an explicit unavailable marker is emitted.
    /// </param>
    /// <param name="patternSummary">
    ///     Optional detected pattern summary appended to the manifest, or <c>null</c> to omit it.
    /// </param>
    /// <returns>
    ///     The rendered manifest block terminated by a newline, or <see cref="string.Empty" /> when
    ///     <paramref name="emittedFiles" /> is empty.
    /// </returns>
    /// <remarks>
    ///     Files are always listed sorted by path using <see cref="StringComparer.OrdinalIgnoreCase" />,
    ///     independent of the order in which they were emitted. <see cref="OutputFormat.Json" /> produces a
    ///     JSON object; all other formats produce YAML-style lines wrapped in an HTML comment block.
    /// </remarks>
    public static string Build(
        IReadOnlyList<FileTokenInfo> emittedFiles,
        OutputFormat format,
        GitStatsResult? gitStats = null,
        PatternSummary? patternSummary = null)
    {
        if (emittedFiles.Count == 0)
            return string.Empty;

        return format switch
        {
            OutputFormat.Json => BuildJsonManifest(emittedFiles, gitStats, patternSummary),
            OutputFormat.Markdown => BuildCommentManifest(emittedFiles, gitStats, patternSummary, prefix: "<!-- fuse:manifest", suffix: "-->"),
            _ => BuildCommentManifest(emittedFiles, gitStats, patternSummary, prefix: "<!-- fuse:manifest", suffix: "-->"),
        };
    }

    private static string BuildCommentManifest(
        IReadOnlyList<FileTokenInfo> emittedFiles,
        GitStatsResult? gitStats,
        PatternSummary? patternSummary,
        string prefix,
        string suffix)
    {
        var lines = BuildManifestLines(emittedFiles, gitStats, patternSummary);
        // Wrap YAML-style lines in an HTML comment block for markdown/xml output formats.
        return prefix + "\n" + string.Join("\n", lines) + "\n" + suffix + "\n";
    }

    private static string BuildJsonManifest(
        IReadOnlyList<FileTokenInfo> emittedFiles,
        GitStatsResult? gitStats,
        PatternSummary? patternSummary)
    {
        var manifest = new JsonManifestDto
        {
            Files = emittedFiles
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Select(f =>
                {
                    var entry = new JsonManifestFileDto
                    {
                        Path = f.Path,
                        Tokens = f.Count,
                    };

                    if (gitStats?.IsAvailable == true &&
                        gitStats.StatsByPath.TryGetValue(f.Path, out var stats))
                    {
                        entry.Commits = stats.CommitCount;
                        entry.LastModified = stats.LastModified?.ToString("yyyy-MM-dd");
                    }

                    return entry;
                })
                .ToArray(),
        };

        if (patternSummary is { Patterns.Count: > 0 })
        {
            manifest.Patterns = patternSummary.Patterns
                .Select(p => new JsonPatternDto
                {
                    Name = p.PatternName,
                    Summary = p.Summary,
                })
                .ToArray();
        }

        if (gitStats is { IsAvailable: false })
            manifest.Git = "unavailable";

        return JsonSerializer.Serialize(manifest, FuseEmissionJsonContext.Default.JsonManifestDto) + "\n";
    }

    private static List<string> BuildManifestLines(
        IReadOnlyList<FileTokenInfo> emittedFiles,
        GitStatsResult? gitStats,
        PatternSummary? patternSummary)
    {
        var lines = new List<string>
        {
            $"files: {emittedFiles.Count}",
        };

        foreach (var file in emittedFiles.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var tokenLabel = FormatTokenCount(file.Count);
            var line = $"  {file.Path} (~{tokenLabel} tokens)";

            if (gitStats?.IsAvailable == true &&
                gitStats.StatsByPath.TryGetValue(file.Path, out var stats))
            {
                var lastModified = stats.LastModified?.ToString("yyyy-MM-dd") ?? "unknown";
                line += $" [commits:{stats.CommitCount} last:{lastModified}]";
            }

            lines.Add(line);
        }

        if (gitStats is { IsAvailable: false })
            lines.Add("  git: unavailable (not a git repository or git not on PATH)");

        if (patternSummary is { Patterns.Count: > 0 })
        {
            foreach (var pattern in patternSummary.Patterns)
                lines.Add($"  pattern: {pattern.PatternName}: {pattern.Summary}");
        }

        return lines;
    }

    private static string FormatTokenCount(long count) =>
        count >= 1000 ? $"{count / 1000.0:F1}k" : count.ToString();
}
