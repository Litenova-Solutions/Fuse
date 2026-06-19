using Fuse.Analysis.Git;
using Fuse.Analysis.Patterns;
using Fuse.Emission.Manifest;
using Fuse.Emission.Models;
using Fuse.Emission.Writers;
using Fuse.Reduction.Models;

namespace Fuse.Emission;

/// <summary>
///     Emits fused content within token budgets, delegating output to an <see cref="IOutputWriter" />.
/// </summary>
/// <remarks>
///     Writes the manifest prefix first when enabled, then entries in descending token-count order.
///     Does not read source files or apply reduction; callers supply <see cref="FusedContent" /> entries.
/// </remarks>
public sealed class EmissionPipeline
{
    /// <summary>
    ///     Approximate token overhead for entry markers and newlines per entry.
    /// </summary>
    public const int MarkerOverheadTokens = 30;

    /// <summary>
    ///     Emits fused content in descending content-length order with token budgeting and split handling.
    /// </summary>
    /// <param name="entries">Reduced content entries to emit.</param>
    /// <param name="options">Emission and output options.</param>
    /// <param name="writer">The output sink for manifest prefix and file entries.</param>
    /// <param name="manifestPatternSummary">Optional pattern summary included in the manifest.</param>
    /// <param name="gitStats">Optional git stats included in the manifest.</param>
    /// <param name="cancellationToken">Token used to cancel writes.</param>
    /// <returns>The completed fusion result from the writer and budget tracker.</returns>
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
