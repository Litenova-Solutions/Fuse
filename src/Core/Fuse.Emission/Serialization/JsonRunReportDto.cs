namespace Fuse.Emission.Serialization;

/// <summary>
///     Machine-readable summary of a fusion run, written when a JSON run report is requested.
/// </summary>
public sealed class JsonRunReportDto
{
    /// <summary>
    ///     Discriminator for run-report records (<c>report</c>).
    /// </summary>
    public string Type { get; set; } = "report";

    /// <summary>
    ///     The tokenizer model or encoding used to count tokens for this run.
    /// </summary>
    public string Tokenizer { get; set; } = string.Empty;

    /// <summary>
    ///     The output format used for this run (<c>xml</c>, <c>markdown</c>, <c>json</c>, or <c>compact</c>).
    /// </summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>
    ///     The total token count across all emitted content.
    /// </summary>
    public long TotalTokens { get; set; }

    /// <summary>
    ///     The number of files emitted.
    /// </summary>
    public int ProcessedFiles { get; set; }

    /// <summary>
    ///     The number of files considered for emission.
    /// </summary>
    public int TotalFiles { get; set; }

    /// <summary>
    ///     The wall-clock duration of the run, in seconds.
    /// </summary>
    public double DurationSeconds { get; set; }

    /// <summary>
    ///     The number of reduction cache hits.
    /// </summary>
    public int CacheHits { get; set; }

    /// <summary>
    ///     The number of reduction cache misses.
    /// </summary>
    public int CacheMisses { get; set; }

    /// <summary>
    ///     The output file paths written to disk. Empty for in-memory runs.
    /// </summary>
    public string[] OutputPaths { get; set; } = [];

    /// <summary>
    ///     Per-file token counts for the emitted entries.
    /// </summary>
    public JsonRunReportFileDto[] Files { get; set; } = [];

    /// <summary>
    ///     The names of any detected patterns, or <c>null</c> when pattern detection was not requested.
    /// </summary>
    public string[]? Patterns { get; set; }
}
