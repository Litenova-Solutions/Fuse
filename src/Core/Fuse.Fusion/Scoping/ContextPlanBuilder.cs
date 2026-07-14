using Fuse.Plugins.Abstractions.Options;
using Fuse.Scoping;

namespace Fuse.Fusion;

/// <summary>
///     Builds the shared <see cref="ContextPlan" /> for a scoped fusion result: it assigns each selected file a
///     role and a render tier once, replacing the former inline inference of seed versus neighbour from the
///     provenance chain length and the separate tiered-level resolver.
/// </summary>
internal static class ContextPlanBuilder
{
    /// <summary>
    ///     Builds the plan from a scoping result.
    /// </summary>
    /// <param name="request">The fusion request, supplying the base reduction level and the scoping mode.</param>
    /// <param name="experimental">The resolved experimental options (tiered emission flag).</param>
    /// <param name="filterResult">The scoping result: files, provenance, scores, and selected members.</param>
    /// <returns>The explicit context plan.</returns>
    public static ContextPlan Build(
        FusionRequest request,
        ExperimentalOptions experimental,
        FilteredFileSet filterResult)
    {
        var seedLevel = request.Reduction.Level;
        var isChangeMode = request.Focus is null;

        // Tiered emission reduces dependency-expanded neighbours to signature skeletons; it is active only for
        // focus mode (change mode keeps its changed files in full) and only when provenance is available.
        // This matches the former BuildTieredLevelResolver gating exactly, so the plan is behavior-neutral.
        var appliesTieredEmission =
            experimental.TieredEmission && !isChangeMode && filterResult.Provenance is not null;

        var seedTier = RenderTierMapping.FromReductionLevel(seedLevel);
        var items = new List<ContextPlanItem>(filterResult.Files.Count);
        foreach (var file in filterResult.Files)
        {
            var path = file.NormalizedRelativePath;
            IReadOnlyList<string>? chain = null;
            filterResult.Provenance?.TryGetValue(path, out chain);

            // A seed is a directly matched file (no provenance chain, or a chain of length one); anything reached
            // by expanding the graph out from a seed has a longer chain and is a dependency. In change mode every
            // selected file is a changed file kept in full.
            var isSeed = chain is null || chain.Count <= 1;
            var role = isChangeMode ? "changed" : isSeed ? "exact-seed" : "dependency";

            // Non-seed files reduce to a skeleton when tiered emission is active; seeds (and everything when it is
            // off) keep the request's level.
            var tier = appliesTieredEmission && !isSeed ? RenderTier.Skeleton : seedTier;

            var score = 0.0;
            filterResult.Scores?.TryGetValue(path, out score);

            var reasons = new List<string>();
            if (filterResult.SelectedMembers?.TryGetValue(path, out var selectedMembers) == true
                && selectedMembers is { Count: > 0 })
            {
                reasons.Add($"selected members: {string.Join(", ", selectedMembers)}");
            }

            items.Add(new ContextPlanItem(
                Path: path,
                NodeId: null,
                Role: role,
                Tier: tier,
                Score: score,
                EstimatedTokens: 0,
                MustKeep: role is "exact-seed" or "changed",
                Reasons: reasons,
                ProvenanceChain: chain ?? []));
        }

        var mode = isChangeMode ? "changes" : "focus";
        return new ContextPlan(mode, items, [], 0, []);
    }

    /// <summary>
    ///     Whether tiered emission is active for the built plan.
    /// </summary>
    /// <param name="request">The fusion request.</param>
    /// <param name="experimental">The resolved experimental options.</param>
    /// <param name="filterResult">The scoping result.</param>
    /// <returns><see langword="true" /> when dependency neighbours render at skeleton tier.</returns>
    public static bool AppliesTieredEmission(
        FusionRequest request,
        ExperimentalOptions experimental,
        FilteredFileSet filterResult) =>
        experimental.TieredEmission && request.Focus is not null && filterResult.Provenance is not null;

    /// <summary>
    ///     Resolves the reduction level for a file from the shared plan.
    /// </summary>
    /// <param name="plan">The context plan.</param>
    /// <param name="file">The source file.</param>
    /// <param name="defaultLevel">The level used when the file is not in the plan.</param>
    /// <returns>The planned reduction level.</returns>
    public static ReductionLevel TierFor(ContextPlan plan, Collection.Models.SourceFile file, ReductionLevel defaultLevel)
    {
        foreach (var item in plan.Items)
        {
            if (string.Equals(item.Path, file.NormalizedRelativePath, StringComparison.OrdinalIgnoreCase))
                return RenderTierMapping.ToReductionLevel(item.Tier);
        }

        return defaultLevel;
    }
}
