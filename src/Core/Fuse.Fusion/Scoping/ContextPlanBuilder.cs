using Fuse.Plugins.Abstractions.Options;

namespace Fuse.Fusion;

/// <summary>
///     Builds the explicit <see cref="ContextPlan" /> for a scoped result (architecture enabler A1): it assigns
///     each selected file a role and a reduction tier once, replacing the former inline inference of seed versus
///     neighbour from the provenance chain length and the separate tiered-level resolver.
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
        var isChangeMode = request.Focus is null && request.Query is null;

        // Tiered emission reduces dependency-expanded neighbours to signature skeletons; it is active only for
        // query and focus (change mode keeps its changed files in full) and only when provenance is available.
        // This matches the former BuildTieredLevelResolver gating exactly, so the plan is behavior-neutral.
        var appliesTieredEmission =
            experimental.TieredEmission && !isChangeMode && filterResult.Provenance is not null;

        var planned = new List<PlannedFile>(filterResult.Files.Count);
        foreach (var file in filterResult.Files)
        {
            var path = file.NormalizedRelativePath;
            IReadOnlyList<string>? chain = null;
            filterResult.Provenance?.TryGetValue(path, out chain);

            // A seed is a directly matched file (no provenance chain, or a chain of length one); anything reached
            // by expanding the graph out from a seed has a longer chain and is a dependency. In change mode every
            // selected file is a changed file kept in full.
            var isSeed = chain is null || chain.Count <= 1;
            var role = isChangeMode ? PlannedRole.Changed : isSeed ? PlannedRole.Seed : PlannedRole.Dependency;

            // Non-seed files reduce to a skeleton when tiered emission is active; seeds (and everything when it is
            // off) keep the request's level.
            var tier = appliesTieredEmission && !isSeed ? ReductionLevel.Skeleton : seedLevel;

            var score = 0.0;
            filterResult.Scores?.TryGetValue(path, out score);

            IReadOnlySet<string>? selectedMembers = null;
            filterResult.SelectedMembers?.TryGetValue(path, out selectedMembers);

            planned.Add(new PlannedFile(
                file,
                role,
                tier,
                score,
                chain,
                selectedMembers,
                MustKeep: role is PlannedRole.Seed or PlannedRole.Changed));
        }

        return new ContextPlan(planned, appliesTieredEmission) { DefaultTier = seedLevel };
    }
}
