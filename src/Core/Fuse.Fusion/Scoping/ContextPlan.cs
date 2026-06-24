using Fuse.Collection.Models;
using Fuse.Plugins.Abstractions.Options;

namespace Fuse.Fusion;

/// <summary>
///     The role a planned file plays in a scoped result (architecture enabler A1). The role is made explicit at
///     planning time instead of being inferred downstream from a provenance chain's length, so reduction,
///     packing, and emission can read it directly.
/// </summary>
internal enum PlannedRole
{
    /// <summary>A query- or focus-matched file, or (in change mode) a changed file: the result's anchor.</summary>
    Seed,

    /// <summary>A file pulled in by following the dependency graph out from a seed.</summary>
    Dependency,

    /// <summary>A changed file in change-scoped fusion, kept in full so the diff's targets are present.</summary>
    Changed,
}

/// <summary>
///     One file's plan: its role, the reduction tier it will be reduced at, its relevance score, provenance
///     chain, any query-selected members, and whether it must survive a budget cut.
/// </summary>
/// <param name="File">The source file.</param>
/// <param name="Role">The file's role in the result.</param>
/// <param name="Tier">The reduction level the file will be reduced at.</param>
/// <param name="Score">The relevance score from scoping, or <c>0</c> when not scored.</param>
/// <param name="Provenance">The provenance chain that admitted the file, when scoping recorded one.</param>
/// <param name="SelectedMembers">The members a query matched in the file, when symbol-level packing applies.</param>
/// <param name="MustKeep">Whether the file must be kept under a token budget (seeds and changed files).</param>
internal sealed record PlannedFile(
    SourceFile File,
    PlannedRole Role,
    ReductionLevel Tier,
    double Score,
    IReadOnlyList<string>? Provenance,
    IReadOnlySet<string>? SelectedMembers,
    bool MustKeep);

/// <summary>
///     The explicit plan for a scoped result (A1): one <see cref="PlannedFile" /> per selected file, carrying the
///     role and reduction tier that were previously inferred ad hoc from the provenance chain length. Built once
///     after scoping and consumed by the reduction stage (per-file tier) and available to packing and emission.
/// </summary>
internal sealed class ContextPlan
{
    private readonly Dictionary<string, PlannedFile> _byPath;

    /// <summary>Initializes the plan over its planned files.</summary>
    /// <param name="files">The planned files, in scoping order.</param>
    /// <param name="appliesTieredEmission">
    ///     Whether tiered emission is active for this run: when true a non-seed file's <see cref="PlannedFile.Tier" />
    ///     is a signature skeleton, and the reduction stage applies the per-file tier; when false every file uses
    ///     the request's reduction level uniformly.
    /// </param>
    public ContextPlan(IReadOnlyList<PlannedFile> files, bool appliesTieredEmission)
    {
        Files = files;
        AppliesTieredEmission = appliesTieredEmission;
        _byPath = new Dictionary<string, PlannedFile>(files.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var planned in files)
            _byPath[planned.File.NormalizedRelativePath] = planned;
    }

    /// <summary>The planned files, in scoping order.</summary>
    public IReadOnlyList<PlannedFile> Files { get; }

    /// <summary>Whether the per-file reduction tier varies (tiered emission active) rather than being uniform.</summary>
    public bool AppliesTieredEmission { get; }

    /// <summary>The reduction tier planned for a file, or the supplied default when the file is not in the plan.</summary>
    /// <param name="file">The source file.</param>
    /// <returns>The planned reduction level.</returns>
    public ReductionLevel TierFor(SourceFile file) =>
        _byPath.TryGetValue(file.NormalizedRelativePath, out var planned) ? planned.Tier : DefaultTier;

    /// <summary>The reduction level used for a file with no explicit plan entry (the request's level).</summary>
    public ReductionLevel DefaultTier { get; init; }
}
