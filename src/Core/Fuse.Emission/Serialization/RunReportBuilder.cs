using System.Text.Json;
using Fuse.Emission.Models;

namespace Fuse.Emission.Serialization;

/// <summary>
///     Builds the machine-readable JSON run report for a completed fusion run.
/// </summary>
/// <remarks>
///     Serialization uses the source-generated <see cref="FuseEmissionJsonContext" />, so the report is
///     AOT-safe. The report always names the tokenizer used for its token counts.
/// </remarks>
public static class RunReportBuilder
{
    /// <summary>
    ///     Builds a JSON run report from a fusion result and the emission options that produced it.
    /// </summary>
    /// <param name="result">The completed fusion result.</param>
    /// <param name="options">The emission options used for the run.</param>
    /// <returns>The run report as a JSON string.</returns>
    public static string Build(FusionResult result, EmissionOptions options)
    {
        var dto = new JsonRunReportDto
        {
            Tokenizer = options.TokenizerModel,
            Format = options.Format.ToString().ToLowerInvariant(),
            TotalTokens = result.TotalTokens,
            ProcessedFiles = result.ProcessedFileCount,
            TotalFiles = result.TotalFileCount,
            DurationSeconds = Math.Round(result.Duration.TotalSeconds, 3),
            CacheHits = result.ReductionCacheHits,
            CacheMisses = result.ReductionCacheMisses,
            OutputPaths = [.. result.GeneratedPaths],
            Files = [.. result.EmittedFileTokens.Select(f => new JsonRunReportFileDto { Path = f.Path, Tokens = f.Count })],
            Patterns = result.PatternSummary is { Patterns.Count: > 0 }
                ? [.. result.PatternSummary.Patterns.Select(p => p.PatternName)]
                : null,
        };

        return JsonSerializer.Serialize(dto, FuseEmissionJsonContext.Default.JsonRunReportDto);
    }
}
