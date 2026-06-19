using Fuse.Emission.Manifest;
using Fuse.Emission.Models;
using Fuse.Emission.Writers;
using Fuse.Reduction.Models;

namespace Fuse.Emission;

/// <summary>
///     Emits fused content within token budgets, delegating output to an <see cref="IOutputWriter" />.
/// </summary>
/// <remarks>
///     <para>
///         Write ordering: when enabled, the manifest prefix is written first, then file entries in
///         descending token-count order so that the largest files appear earliest in the output.
///     </para>
///     <para>
///         Token-budget behavior is driven by <see cref="TokenBudget" />: each entry either continues in
///         the current part (<see cref="BudgetConsumeResult.Continue" />), forces a new part when the writer
///         supports multi-part output (<see cref="BudgetConsumeResult.Split" /> and
///         <see cref="IOutputWriter.SupportsMultiPart" />), or halts emission once the hard
///         <see cref="EmissionOptions.MaxTokens" /> limit is reached (<see cref="BudgetConsumeResult.Halt" />).
///         When a split is required but the writer cannot rotate parts, the entry is forced into the current
///         part instead. Entries marked <see cref="FusedContent.IsTrivial" /> are skipped and excluded from
///         the manifest.
///     </para>
///     <para>
///         This pipeline does not read source files or apply reduction; callers supply
///         <see cref="FusedContent" /> entries. Disk side effects (file creation, splitting, deletion of empty
///         parts) are owned by the supplied <see cref="IOutputWriter" />, not by this type.
///     </para>
/// </remarks>
public sealed class EmissionPipeline
{
    /// <summary>
    ///     Approximate per-entry token overhead reserved for entry markers and newlines, added to each
    ///     entry's token cost when charging it against the budget.
    /// </summary>
    public const int MarkerOverheadTokens = 30;

    /// <summary>
    ///     Asynchronously emits fused content in descending token-count order, applying token budgeting,
    ///     part splitting, and an optional manifest prefix.
    /// </summary>
    /// <param name="entries">Reduced content entries to emit. Trivial entries are skipped.</param>
    /// <param name="options">Emission and output options, including token limits and manifest settings.</param>
    /// <param name="writer">The output sink for the manifest prefix and file entries.</param>
    /// <param name="manifestPatternSummary">
    ///     Optional pattern summary included in the manifest, or <c>null</c> to omit pattern information.
    /// </param>
    /// <param name="gitStats">
    ///     Optional git churn statistics included in the manifest, or <c>null</c> to omit git information.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel writes between entries.</param>
    /// <returns>
    ///     A task whose result is the completed <see cref="FusionResult" />, combining the writer's generated
    ///     output with the total token count tracked by the budget.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    ///     Thrown when <paramref name="cancellationToken" /> is signalled during emission.
    /// </exception>
    /// <remarks>
    ///     Entries are emitted in descending <see cref="FusedContent.TokenCount" /> order. Emission stops early
    ///     once the budget is exhausted; remaining entries are not written.
    /// </remarks>
    public async Task<FusionResult> EmitAsync(
        IReadOnlyList<FusedContent> entries,
        EmissionOptions options,
        IOutputWriter writer,
        PatternSummary? manifestPatternSummary = null,
        GitStatsResult? gitStats = null,
        CancellationToken cancellationToken = default)
    {
        var budget = new TokenBudget(options);
        var orderedEntries = entries
            .OrderByDescending(e => e.TokenCount)
            .ToList();

        if (options.IncludeManifest)
        {
            var manifestFiles = orderedEntries
                .Where(e => !e.IsTrivial)
                .Select(e => new FileTokenInfo(e.NormalizedPath, e.TokenCount))
                .ToList();

            var manifest = ManifestBuilder.Build(
                manifestFiles,
                options.Format,
                gitStats,
                manifestPatternSummary);

            if (!string.IsNullOrEmpty(manifest))
                await writer.WritePrefixAsync(manifest, cancellationToken);
        }

        foreach (var entry in orderedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (budget.IsExhausted)
                break;

            if (entry.IsTrivial)
                continue;

            var entryTokens = entry.TokenCount + MarkerOverheadTokens;

            // Retry after rotating output parts when split is supported; otherwise force the entry through.
            while (true)
            {
                var consumeResult = budget.Consume(entryTokens);

                if (consumeResult == BudgetConsumeResult.Split && writer.SupportsMultiPart)
                {
                    await writer.RotatePartAsync(cancellationToken);
                    budget.ResetCurrentPart();
                    continue;
                }

                if (consumeResult == BudgetConsumeResult.Split)
                    budget.ForceConsume(entryTokens);

                break;
            }

            await writer.WriteEntryAsync(entry, cancellationToken);

            if (budget.IsExhausted)
                break;
        }

        var writerResult = await writer.CompleteAsync(cancellationToken);

        return new FusionResult(
            writerResult.GeneratedPaths,
            writerResult.InMemoryContent,
            budget.TotalTokens,
            writerResult.ProcessedFileCount,
            entries.Count,
            writerResult.Duration,
            writerResult.TopTokenFiles,
            manifestPatternSummary);
    }
}
