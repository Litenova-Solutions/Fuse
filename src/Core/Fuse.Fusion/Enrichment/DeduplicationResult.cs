using Fuse.Reduction.Models;

namespace Fuse.Fusion.Enrichment;

/// <summary>
///     The outcome of leading-header deduplication.
/// </summary>
/// <param name="Content">The entries after rewriting, in the original order.</param>
/// <param name="Preamble">
///     The canonical text of every deduplicated header, prepended once to the output, or <c>null</c> when no
///     header was shared.
/// </param>
/// <param name="HeadersDeduplicated">The number of distinct headers that were deduplicated.</param>
/// <param name="FilesAffected">The number of files whose header was replaced with a marker.</param>
public sealed record DeduplicationResult(
    IReadOnlyList<FusedContent> Content,
    string? Preamble,
    int HeadersDeduplicated,
    int FilesAffected);
