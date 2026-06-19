using Fuse.Emission.Models;
using Fuse.Emission.Writers;
using Fuse.Reduction.Models;

namespace Fuse.Emission;

/// <summary>
///     Emits fused content within token budgets, delegating output to an <see cref="IOutputWriter" />.
/// </summary>
public sealed class EmissionPipeline
{
    /// <summary>
    ///     Approximate token overhead for <c>&lt;file&gt;</c> marker tags and newlines per entry.
    /// </summary>
    public const int MarkerOverheadTokens = 30;

    /// <summary>
    ///     Emits fused content in descending content-length order with token budgeting and split handling.
    /// </summary>
    /// <param name="entries">The fused content entries to emit.</param>
    /// <param name="options">The emission options controlling limits and output behavior.</param>
    /// <param name="writer">The output writer that receives formatted entries.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous emission, returning statistics and output references.</returns>
    public async Task<FusionResult> EmitAsync(
        IReadOnlyList<FusedContent> entries,
        EmissionOptions options,
        IOutputWriter writer,
        CancellationToken cancellationToken = default)
    {
        var budget = new TokenBudget(options);
        var orderedEntries = entries
            .OrderByDescending(e => e.Content.Length)
            .ToList();

        foreach (var entry in orderedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (budget.IsExhausted)
            {
                break;
            }

            if (entry.IsTrivial)
            {
                continue;
            }

            var entryTokens = entry.TokenCount + MarkerOverheadTokens;

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
                {
                    budget.ForceConsume(entryTokens);
                }

                break;
            }

            await writer.WriteEntryAsync(entry, cancellationToken);

            if (budget.IsExhausted)
            {
                break;
            }
        }

        var writerResult = await writer.CompleteAsync(cancellationToken);

        return new FusionResult(
            writerResult.GeneratedPaths,
            writerResult.InMemoryContent,
            budget.TotalTokens,
            writerResult.ProcessedFileCount,
            entries.Count,
            writerResult.Duration,
            writerResult.TopTokenFiles);
    }
}
