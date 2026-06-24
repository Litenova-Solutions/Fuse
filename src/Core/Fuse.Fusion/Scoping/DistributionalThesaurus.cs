namespace Fuse.Fusion.Scoping;

/// <summary>
///     A local distributional thesaurus (Q4): associates a query identifier with the corpus identifiers it most
///     often co-occurs with, by pointwise mutual information over declared symbols in the same file, so a query
///     term can bridge to a related-but-different vocabulary fully lexically, with no model.
/// </summary>
/// <remarks>
///     This is the corpus-global counterpart to pseudo-relevance feedback, which only harvests terms from the
///     top feedback documents. Association is computed per run from the declared symbols already indexed for
///     ranking (no extra reads); persisting the table to <c>.fuse/fuse.db</c> is a follow-on once it earns a
///     place on the default path. Associates are gated by a minimum co-occurrence count and a positive PMI, so
///     a coincidental single co-occurrence does not inject noise.
/// </remarks>
public static class DistributionalThesaurus
{
    // An association must appear together in at least this many files, so a one-off co-occurrence is ignored.
    private const int MinCoDocFrequency = 2;

    /// <summary>
    ///     Expands the supplied query identifier terms with their top PMI-associated identifiers across the
    ///     corpus' declared symbols.
    /// </summary>
    /// <param name="queryTerms">The query's identifier terms (already tokenized and normalized).</param>
    /// <param name="documentSymbolTerms">Per document, the distinct set of its declared-symbol terms.</param>
    /// <param name="topPerTerm">The maximum number of associates to return per query term.</param>
    /// <param name="weight">The weight assigned to each associate, below the original query terms' weight.</param>
    /// <returns>
    ///     The associated terms mapped to <paramref name="weight" />, excluding the query terms themselves.
    ///     Empty when no association clears the gates.
    /// </returns>
    public static IReadOnlyDictionary<string, double> Expand(
        IReadOnlyCollection<string> queryTerms,
        IReadOnlyList<IReadOnlySet<string>> documentSymbolTerms,
        int topPerTerm = 3,
        double weight = 0.3)
    {
        var associates = new Dictionary<string, double>(StringComparer.Ordinal);
        if (queryTerms.Count == 0 || documentSymbolTerms.Count == 0)
            return associates;

        var totalDocuments = documentSymbolTerms.Count;

        // Document frequency of every symbol term, computed once.
        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var terms in documentSymbolTerms)
            foreach (var term in terms)
                documentFrequency[term] = documentFrequency.GetValueOrDefault(term) + 1;

        var queryTermSet = new HashSet<string>(queryTerms, StringComparer.Ordinal);

        foreach (var queryTerm in queryTermSet)
        {
            if (!documentFrequency.TryGetValue(queryTerm, out var queryDf) || queryDf == 0)
                continue;

            // Co-occurrence counts of every symbol with this query term, over the files that contain the query
            // term, so the scan is proportional to the query term's document frequency rather than the corpus.
            var coCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var terms in documentSymbolTerms)
            {
                if (!terms.Contains(queryTerm))
                    continue;

                foreach (var term in terms)
                {
                    if (queryTermSet.Contains(term))
                        continue;

                    coCounts[term] = coCounts.GetValueOrDefault(term) + 1;
                }
            }

            var scored = new List<(string Term, double Pmi)>();
            foreach (var (term, coCount) in coCounts)
            {
                if (coCount < MinCoDocFrequency)
                    continue;

                var termDf = documentFrequency[term];
                // PMI = log( P(q,t) / (P(q) P(t)) ) = log( coCount * N / (queryDf * termDf) ). Positive only.
                var pmi = Math.Log((double)coCount * totalDocuments / ((double)queryDf * termDf));
                if (pmi > 0)
                    scored.Add((term, pmi));
            }

            foreach (var (term, _) in scored.OrderByDescending(s => s.Pmi).ThenBy(s => s.Term, StringComparer.Ordinal).Take(topPerTerm))
            {
                // A term associated with several query terms keeps the single (equal) weight rather than
                // accumulating, so a common associate does not dominate.
                associates[term] = weight;
            }
        }

        return associates;
    }
}
