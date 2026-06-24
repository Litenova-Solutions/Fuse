namespace Fuse.Fusion.Scoping;

/// <summary>
///     Expands a query with recurring declared-symbol terms drawn from the top files of a first-pass ranking
///     (pseudo-relevance feedback), so a sparse natural-language query is rewritten in the codebase's own
///     vocabulary before a second ranking pass.
/// </summary>
/// <remarks>
///     Classical pseudo-relevance feedback assumes the first pass's top documents are relevant and mines them
///     for terms to add to the query. The known failure mode is a weak first pass that feeds back noise. Three
///     guards keep this conservative: candidates are taken only from the high-signal declared-symbol field
///     (type and member names), not file bodies; a term must recur across several feedback files
///     (<see cref="QueryExpansionOptions.MinFeedbackDocs" />); and a term must clear a corpus inverse
///     document frequency floor (<see cref="QueryExpansionOptions.MinExpansionIdf" />), which drops boilerplate
///     names shared across most files. Expansion terms are blended in below the original query's weight, so
///     they bias ranking toward co-occurring concepts without overriding the original intent. The pass is
///     entirely lexical: no model inference, no network.
/// </remarks>
public static class PseudoRelevanceExpander
{
    /// <summary>
    ///     Builds the weighted term query for the second ranking pass: the original query terms at unit weight,
    ///     plus expansion terms harvested from the feedback files at a reduced weight.
    /// </summary>
    /// <param name="query">The original natural-language or keyword query.</param>
    /// <param name="initialRanking">
    ///     The first-pass ranking, most relevant first. Its top files (capped at
    ///     <see cref="QueryExpansionOptions.FeedbackDocs" />) are the feedback set.
    /// </param>
    /// <param name="documents">The indexed documents keyed by normalized relative path, providing each feedback file's declared symbols.</param>
    /// <param name="options">The expansion configuration.</param>
    /// <param name="inverseDocumentFrequency">
    ///     Corpus inverse document frequency for a normalized term, typically
    ///     <see cref="IRelevanceIndex.InverseDocumentFrequency(string)" />. Candidates are ranked by feedback
    ///     weight times IDF and gated by <see cref="QueryExpansionOptions.MinExpansionIdf" />, so discriminative
    ///     terms win and corpus-wide boilerplate is dropped.
    /// </param>
    /// <returns>
    ///     Normalized terms mapped to per-term weights, suitable for
    ///     <see cref="IRelevanceIndex.RankScored(IReadOnlyDictionary{string, double}, int)" />. When expansion
    ///     is disabled, the first pass is too sparse, or no term qualifies, this is just the original query
    ///     terms at unit weight, so re-ranking with it reproduces the first pass.
    /// </returns>
    public static IReadOnlyDictionary<string, double> Expand(
        string query,
        IReadOnlyList<RankedFile> initialRanking,
        IReadOnlyDictionary<string, IndexedDocument> documents,
        QueryExpansionOptions options,
        Func<string, double> inverseDocumentFrequency)
    {
        var weighted = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var term in RelevanceTokenizer.Tokenize(query))
            weighted[term] = 1.0;

        if (!options.Enabled || weighted.Count == 0 || initialRanking.Count < options.MinInitialHits)
            return weighted;

        // Accumulate, per candidate symbol term, how many feedback files declare it and the summed relevance
        // of those files. Document frequency across the feedback set is the noise filter; summed relevance is
        // the ranking signal among the survivors.
        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        var weightAccumulator = new Dictionary<string, double>(StringComparer.Ordinal);

        var feedback = initialRanking.Take(options.FeedbackDocs);
        foreach (var file in feedback)
        {
            if (!documents.TryGetValue(file.Path, out var document) || document.Symbols is null)
                continue;

            var termsInDoc = new HashSet<string>(StringComparer.Ordinal);
            foreach (var symbol in document.Symbols)
                foreach (var term in RelevanceTokenizer.Tokenize(symbol))
                    termsInDoc.Add(term);

            foreach (var term in termsInDoc)
            {
                if (weighted.ContainsKey(term))
                    continue; // already an original query term; keep its full weight

                documentFrequency[term] = documentFrequency.GetValueOrDefault(term) + 1;
                weightAccumulator[term] = weightAccumulator.GetValueOrDefault(term) + Math.Max(file.Score, 0);
            }
        }

        // Rank survivors by feedback weight times corpus IDF so a discriminative concept name outranks a
        // boilerplate suffix shared by most files, and gate out terms below the IDF floor entirely.
        var expansionTerms = weightAccumulator
            .Where(kvp => documentFrequency[kvp.Key] >= options.MinFeedbackDocs)
            .Select(kvp => (Term: kvp.Key, Score: kvp.Value * inverseDocumentFrequency(kvp.Key), Idf: inverseDocumentFrequency(kvp.Key)))
            .Where(c => c.Idf >= options.MinExpansionIdf)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Term, StringComparer.Ordinal)
            .Take(options.ExpansionTerms);

        foreach (var (term, _, _) in expansionTerms)
            weighted[term] = options.ExpansionWeight;

        return weighted;
    }

    /// <summary>
    ///     Merges a first-pass ranking with the expanded second-pass ranking so that expansion can only add
    ///     seeds, never drop one the first pass already found.
    /// </summary>
    /// <param name="firstPass">The original BM25F ranking.</param>
    /// <param name="reranked">The ranking after query expansion.</param>
    /// <returns>
    ///     The union of both rankings ordered by score descending: every reranked file at its expanded score,
    ///     plus any first-pass file the expansion demoted out of its window, re-added at its original score.
    /// </returns>
    /// <remarks>
    ///     Pseudo-relevance feedback can move a genuinely relevant first-pass seed below the cutoff when the
    ///     query is poorly aligned with the change (a vague title against a specific edit). Keeping the union
    ///     makes expansion a recall-only operation: a file strong under either the original or the expanded
    ///     query survives as a seed, so a misfiring expansion cannot lower recall below the first pass. Seeds
    ///     are admitted ahead of graph neighbours during expansion, so the cost is at most a few extra seed
    ///     files, not unbounded breadth.
    /// </remarks>
    public static IReadOnlyList<RankedFile> MergePreservingSeeds(
        IReadOnlyList<RankedFile> firstPass,
        IReadOnlyList<RankedFile> reranked)
    {
        var merged = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in reranked)
            merged[file.Path] = file.Score;

        foreach (var file in firstPass)
            if (!merged.ContainsKey(file.Path))
                merged[file.Path] = file.Score;

        return merged
            .Select(kvp => new RankedFile(kvp.Key, kvp.Value))
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
