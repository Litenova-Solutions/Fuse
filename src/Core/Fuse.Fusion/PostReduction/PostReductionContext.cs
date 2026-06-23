using Fuse.Collection.Options;
using Fuse.Collection.Models;
using Fuse.Emission.Writers;
using Fuse.Reduction.Models;
using Fuse.Reduction.Tokenization;

namespace Fuse.Fusion.PostReduction;

/// <summary>
///     Inputs for the post-reduction enrichment and emission stage.
/// </summary>
/// <param name="Request">The fusion request driving emission and enrichment options.</param>
/// <param name="ReducedContent">Per-file reduced content after scoping transforms.</param>
/// <param name="CollectedFiles">The full collected file set, used for structural map generation.</param>
/// <param name="Provenance">Optional dependency hop chains from scoping expansion.</param>
/// <param name="Scores">Optional relevance scores from scoping.</param>
/// <param name="SelectedMembers">Optional qualified member names for symbol-level packing.</param>
/// <param name="ReviewPreamble">Optional review map preamble from change scoping.</param>
/// <param name="TokenCounter">The tokenizer used for token counts during enrichment.</param>
/// <param name="EntryFormatter">The formatter used when constructing the output writer.</param>
public sealed record PostReductionContext(
    FusionRequest Request,
    IReadOnlyList<FusedContent> ReducedContent,
    IReadOnlyList<SourceFile> CollectedFiles,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Provenance,
    IReadOnlyDictionary<string, double>? Scores,
    IReadOnlyDictionary<string, IReadOnlySet<string>>? SelectedMembers,
    string? ReviewPreamble,
    ITokenCounter TokenCounter,
    IEntryFormatter EntryFormatter);
