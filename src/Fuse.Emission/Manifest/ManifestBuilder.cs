using System.Text;
using System.Text.Json;
using Fuse.Analysis.Git;
using Fuse.Analysis.Patterns;
using Fuse.Emission.Models;

namespace Fuse.Emission.Manifest;

/// <summary>
///     Builds the manifest header prepended before fused file entries.
/// </summary>
public static class ManifestBuilder
{
    /// <summary>
    ///     Renders a manifest block for emitted files in the requested output format.
    /// </summary>
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
        return prefix + "\n" + string.Join("\n", lines) + "\n" + suffix + "\n";
    }

    private static string BuildJsonManifest(
        IReadOnlyList<FileTokenInfo> emittedFiles,
        GitStatsResult? gitStats,
        PatternSummary? patternSummary)
    {
        var manifest = new Dictionary<string, object?>
        {
            ["type"] = "manifest",
            ["files"] = emittedFiles
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Select(f =>
                {
                    var entry = new Dictionary<string, object?>
                    {
                        ["path"] = f.Path,
                        ["tokens"] = f.Count,
                    };

                    if (gitStats?.IsAvailable == true &&
                        gitStats.StatsByPath.TryGetValue(f.Path, out var stats))
                    {
                        entry["commits"] = stats.CommitCount;
                        entry["lastModified"] = stats.LastModified?.ToString("yyyy-MM-dd");
                    }

                    return entry;
                })
                .ToArray(),
        };

        if (patternSummary is { Patterns.Count: > 0 })
        {
            manifest["patterns"] = patternSummary.Patterns
                .Select(p => new Dictionary<string, string>
                {
                    ["name"] = p.PatternName,
                    ["summary"] = p.Summary,
                })
                .ToArray();
        }

        if (gitStats is { IsAvailable: false })
            manifest["git"] = "unavailable";

        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + "\n";
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
