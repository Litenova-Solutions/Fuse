
namespace Fuse.Emission.Models;

/// <summary>
///     Represents the result of a fusion emission operation.
/// </summary>
public sealed class FusionResult
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="FusionResult" /> class.
    /// </summary>
    /// <param name="generatedPaths">The output file paths produced by disk emission.</param>
    /// <param name="inMemoryContent">The fused content produced by in-memory emission, or <c>null</c> for disk-only emission.</param>
    /// <param name="totalTokens">The total number of tokens across all emitted content.</param>
    /// <param name="processedFileCount">The number of files successfully emitted.</param>
    /// <param name="totalFileCount">The total number of files considered for emission.</param>
    /// <param name="duration">The wall-clock duration of the emission operation.</param>
    /// <param name="topTokenFiles">The files consuming the most tokens, ordered by descending token count.</param>
    /// <param name="patternSummary">The detected pattern summary, or <c>null</c> when pattern detection was not requested.</param>
    /// <param name="reductionCacheHits">The number of reduction cache hits for the fusion run.</param>
    /// <param name="reductionCacheMisses">The number of reduction cache misses for the fusion run.</param>
    /// <param name="emittedFileTokens">
    ///     Per-file token counts for all emitted entries, or <c>null</c> to default to an empty list.
    /// </param>
    /// <param name="plan">The scoped result's context plan, or <c>null</c> to default to an empty list.</param>
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
        IReadOnlyList<FileTokenInfo>? emittedFileTokens = null,
        IReadOnlyList<PlannedFileInfo>? plan = null)
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
        Plan = plan ?? Array.Empty<PlannedFileInfo>();
    }

    /// <summary>
    ///     The output file paths produced by disk emission. Empty for in-memory emission.
    /// </summary>
    public IReadOnlyList<string> GeneratedPaths { get; }

    /// <summary>
    ///     The fused content produced by in-memory emission, or <c>null</c> for disk-only emission.
    /// </summary>
    public string? InMemoryContent { get; }

    /// <summary>
    ///     The total number of tokens across all emitted content.
    /// </summary>
    public long TotalTokens { get; }

    /// <summary>
    ///     The number of files successfully emitted.
    /// </summary>
    public int ProcessedFileCount { get; }

    /// <summary>
    ///     The total number of files considered for emission.
    /// </summary>
    public int TotalFileCount { get; }

    /// <summary>
    ///     The wall-clock duration of the emission operation.
    /// </summary>
    public TimeSpan Duration { get; }

    /// <summary>
    ///     The files consuming the most tokens, ordered by descending token count.
    /// </summary>
    public IReadOnlyList<FileTokenInfo> TopTokenFiles { get; }

    /// <summary>
    ///     The detected pattern summary, or <c>null</c> when pattern detection was not requested.
    /// </summary>
    public PatternSummary? PatternSummary { get; }

    /// <summary>
    ///     The number of reduction cache hits for the fusion run.
    /// </summary>
    public int ReductionCacheHits { get; }

    /// <summary>
    ///     The number of reduction cache misses for the fusion run.
    /// </summary>
    public int ReductionCacheMisses { get; }

    /// <summary>
    ///     Per-file token counts for all emitted entries.
    /// </summary>
    public IReadOnlyList<FileTokenInfo> EmittedFileTokens { get; }

    /// <summary>
    ///     The scoped result's context plan: one entry per planned file with its role, reduction tier, and score.
    ///     Empty for unscoped runs and for paths that do not build a plan. Surfaced for explain surfaces (the
    ///     VS Code extension) so an agent or developer can see why each file was included and at what fidelity.
    /// </summary>
    public IReadOnlyList<PlannedFileInfo> Plan { get; }
}
