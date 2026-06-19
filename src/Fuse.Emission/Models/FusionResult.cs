using Fuse.Analysis.Patterns;

namespace Fuse.Emission.Models;

/// <summary>
///     Represents the result of a fusion emission operation.
/// </summary>
public sealed class FusionResult
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="FusionResult" /> class.
    /// </summary>
    public FusionResult(
        IReadOnlyList<string> generatedPaths,
        string? inMemoryContent,
        long totalTokens,
        int processedFileCount,
        int totalFileCount,
        TimeSpan duration,
        IReadOnlyList<FileTokenInfo> topTokenFiles,
        PatternSummary? patternSummary = null,
        int reductionCacheHits = 0,
        int reductionCacheMisses = 0,
        IReadOnlyList<FileTokenInfo>? emittedFileTokens = null)
    {
        GeneratedPaths = generatedPaths;
        InMemoryContent = inMemoryContent;
        TotalTokens = totalTokens;
        ProcessedFileCount = processedFileCount;
        TotalFileCount = totalFileCount;
        Duration = duration;
        TopTokenFiles = topTokenFiles;
        PatternSummary = patternSummary;
        ReductionCacheHits = reductionCacheHits;
        ReductionCacheMisses = reductionCacheMisses;
        EmittedFileTokens = emittedFileTokens ?? Array.Empty<FileTokenInfo>();
    }

    /// <summary>
    ///     Gets the file paths produced by disk emission.
    /// </summary>
    public IReadOnlyList<string> GeneratedPaths { get; }

    /// <summary>
    ///     Gets the fused content produced by in-memory emission, or <c>null</c> for disk-only emission.
    /// </summary>
    public string? InMemoryContent { get; }

    /// <summary>
    ///     Gets the total number of tokens across all emitted content.
    /// </summary>
    public long TotalTokens { get; }

    /// <summary>
    ///     Gets the number of files successfully emitted.
    /// </summary>
    public int ProcessedFileCount { get; }

    /// <summary>
    ///     Gets the total number of files considered for emission.
    /// </summary>
    public int TotalFileCount { get; }

    /// <summary>
    ///     Gets the duration of the emission operation.
    /// </summary>
    public TimeSpan Duration { get; }

    /// <summary>
    ///     Gets the top files consuming the most tokens.
    /// </summary>
    public IReadOnlyList<FileTokenInfo> TopTokenFiles { get; }

    /// <summary>
    ///     Gets the detected pattern summary, or <c>null</c> when pattern detection was not requested.
    /// </summary>
    public PatternSummary? PatternSummary { get; }

    /// <summary>
    ///     Gets the number of reduction cache hits for the fusion run.
    /// </summary>
    public int ReductionCacheHits { get; }

    /// <summary>
    ///     Gets the number of reduction cache misses for the fusion run.
    /// </summary>
    public int ReductionCacheMisses { get; }

    /// <summary>
    ///     Gets per-file token counts for all emitted entries.
    /// </summary>
    public IReadOnlyList<FileTokenInfo> EmittedFileTokens { get; }
}
