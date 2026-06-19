namespace Fuse.Emission.Models;

/// <summary>
///     Represents the result of a fusion emission operation.
/// </summary>
public sealed class FusionResult
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="FusionResult" /> class.
    /// </summary>
    /// <param name="generatedPaths">The file paths produced by disk emission.</param>
    /// <param name="inMemoryContent">The fused content produced by in-memory emission, if any.</param>
    /// <param name="totalTokens">The total number of tokens across all emitted content.</param>
    /// <param name="processedFileCount">The number of files successfully emitted.</param>
    /// <param name="totalFileCount">The total number of files considered for emission.</param>
    /// <param name="duration">The duration of the emission operation.</param>
    /// <param name="topTokenFiles">The top files consuming the most tokens.</param>
    public FusionResult(
        IReadOnlyList<string> generatedPaths,
        string? inMemoryContent,
        long totalTokens,
        int processedFileCount,
        int totalFileCount,
        TimeSpan duration,
        IReadOnlyList<FileTokenInfo> topTokenFiles)
    {
        GeneratedPaths = generatedPaths;
        InMemoryContent = inMemoryContent;
        TotalTokens = totalTokens;
        ProcessedFileCount = processedFileCount;
        TotalFileCount = totalFileCount;
        Duration = duration;
        TopTokenFiles = topTokenFiles;
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
}
