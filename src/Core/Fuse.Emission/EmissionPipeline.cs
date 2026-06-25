using Fuse.Emission.Manifest;
using Fuse.Emission.Models;
using Fuse.Emission.Writers;
using Fuse.Plugins.Abstractions.Outline;
using Fuse.Reduction.Models;
using Fuse.Reduction.Tokenization;
using Fuse.Collection.FileSystem;

namespace Fuse.Emission;

/// <summary>
///     Emits fused content within token budgets, delegating output to an <see cref="IOutputWriter" />.
/// </summary>
/// <remarks>
///     <para>
///         Write ordering: when enabled, the manifest prefix is written first. File entries follow in
///         descending relevance order when scoping assigned relevance scores, so the files closest to the
///         seed survive a token budget; otherwise entries are written in descending token-count order so the
///         largest files appear earliest.
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
        var orderedEntries = OrderEntries(entries);

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

        var writtenCount = 0;
        foreach (var entry in orderedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (budget.IsExhausted)
                break;

            if (entry.IsTrivial)
                continue;

            var entryTokens = entry.TokenCount + MarkerOverheadTokens;

            // Rotate output parts when split is supported; otherwise force the entry into the current part.
            var consumeResult = budget.Consume(entryTokens);
            while (consumeResult == BudgetConsumeResult.Split && writer.SupportsMultiPart)
            {
                await writer.RotatePartAsync(cancellationToken);
                budget.ResetCurrentPart();
                consumeResult = budget.Consume(entryTokens);
            }

            if (consumeResult == BudgetConsumeResult.Split)
            {
                // Single-part writer cannot rotate, so force the split entry into the current part.
                budget.ForceConsume(entryTokens);
                consumeResult = BudgetConsumeResult.Continue;
            }

            if (consumeResult == BudgetConsumeResult.Halt)
            {
                // The hard limit would be breached. Emit the single most-relevant entry unconditionally (it may
                // alone exceed the budget) so a scoped run never emits nothing; otherwise stop without writing
                // the over-budget entry.
                if (writtenCount == 0)
                {
                    budget.ForceConsume(entryTokens);
                    await writer.WriteEntryAsync(entry, cancellationToken);
                    writtenCount++;
                }

                break;
            }

            await writer.WriteEntryAsync(entry, cancellationToken);
            writtenCount++;
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
            manifestPatternSummary,
            emittedFileTokens: writerResult.EmittedFileTokens);
    }

    /// <summary>
    ///     Emits a table-of-contents document for the reduced file set, degrading detail when a token budget is
    ///     configured.
    /// </summary>
    /// <param name="reducedContent">Reduced entries whose paths and token costs appear in the map.</param>
    /// <param name="options">Emission options, including format and optional <see cref="EmissionOptions.TableOfContentsMaxTokens" />.</param>
    /// <param name="inMemory">When <see langword="true" />, capture output in memory instead of writing to disk.</param>
    /// <param name="fileSystem">File system used for disk output.</param>
    /// <param name="resolveSymbolsAsync">
    ///     Resolves the symbol outline for an entry from original source (independent of reduction mode).
    /// </param>
    /// <param name="tokenCounter">Token counter used to measure the document and enforce the TOC budget.</param>
    /// <param name="cancellationToken">Token used to cancel symbol resolution and writes.</param>
    /// <returns>The completed fusion result containing the table of contents and per-file token costs.</returns>
    public async Task<FusionResult> EmitTableOfContentsAsync(
        IReadOnlyList<FusedContent> reducedContent,
        EmissionOptions options,
        bool inMemory,
        IFileSystem fileSystem,
        Func<FusedContent, CancellationToken, Task<IReadOnlyList<OutlineSymbol>>> resolveSymbolsAsync,
        ITokenCounter tokenCounter,
        CancellationToken cancellationToken = default)
    {
        var entries = new List<TableOfContentsFileEntry>(reducedContent.Count);
        foreach (var item in reducedContent)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (item.IsTrivial)
                continue;

            var symbols = await resolveSymbolsAsync(item, cancellationToken);
            entries.Add(new TableOfContentsFileEntry(item.NormalizedPath, item.TokenCount, symbols));
        }

        var (document, totalTokens) = BuildTocWithinBudget(entries, options, tokenCounter);
        var emittedFileTokens = entries
            .Select(e => new FileTokenInfo(e.Path, e.Tokens))
            .ToArray();

        await using var writer = new TableOfContentsOutputWriter(options, inMemory, fileSystem, emittedFileTokens);
        await writer.WritePrefixAsync(document, cancellationToken);
        var writerResult = await writer.CompleteAsync(cancellationToken);

        return writerResult with
        {
            TotalTokens = totalTokens,
            TotalFileCount = reducedContent.Count,
        };
    }

    /// <summary>
    ///     Renders the table of contents at the highest detail level that fits the configured token budget.
    /// </summary>
    private static (string Document, int Tokens) BuildTocWithinBudget(
        IReadOnlyList<TableOfContentsFileEntry> entries,
        EmissionOptions emission,
        ITokenCounter tokenCounter)
    {
        var document = TableOfContentsBuilder.Build(entries, emission.Format, TableOfContentsDetail.Full);
        var tokens = tokenCounter.Count(document);

        if (emission.TableOfContentsMaxTokens is not int budget || tokens <= budget)
            return (document, tokens);

        foreach (var detail in (ReadOnlySpan<TableOfContentsDetail>)[TableOfContentsDetail.PathsOnly, TableOfContentsDetail.Directories])
        {
            document = TableOfContentsBuilder.Build(entries, emission.Format, detail);
            tokens = tokenCounter.Count(document);
            if (tokens <= budget)
                break;
        }

        return (document, tokens);
    }

    /// <summary>
    ///     Orders entries for emission. When any entry carries a relevance score (a scoped run), entries are
    ///     emitted most-relevant first so that the files closest to the seed survive a token budget; ties and
    ///     unscored entries fall back to descending token count. When no entry is scored, the historical
    ///     descending-token-count order is preserved.
    /// </summary>
    private static List<FusedContent> OrderEntries(IReadOnlyList<FusedContent> entries)
    {
        var anyScored = false;
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].RelevanceScore is not null)
            {
                anyScored = true;
                break;
            }
        }

        if (!anyScored)
            return entries.OrderByDescending(e => e.TokenCount).ToList();

        return entries
            .OrderByDescending(e => e.RelevanceScore ?? double.NegativeInfinity)
            .ThenByDescending(e => e.TokenCount)
            .ThenBy(e => e.NormalizedPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
