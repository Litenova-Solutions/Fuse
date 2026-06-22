using Fuse.Reduction.Models;

namespace Fuse.Fusion.Enrichment;

/// <summary>
///     The outcome of near-duplicate member-body deduplication.
/// </summary>
/// <param name="Content">The entries after rewriting, in the original order.</param>
/// <param name="BodiesDeduplicated">The number of distinct member bodies that had at least one duplicate replaced.</param>
/// <param name="MembersRewritten">The number of duplicate member bodies replaced with a reference marker.</param>
public sealed record BodyDeduplicationResult(
    IReadOnlyList<FusedContent> Content,
    int BodiesDeduplicated,
    int MembersRewritten);
