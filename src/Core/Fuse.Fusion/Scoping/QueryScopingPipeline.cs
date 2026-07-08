using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions.Scoping;
using Fuse.Collection.Models;
using Fuse.Collection.FileSystem;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Dependencies;
using Fuse.Plugins.Abstractions.Outline;
using Fuse.Reduction.Caching;
using Fuse.Reduction.Tokenization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fuse.Fusion;

/// <summary>
///     The query-mode scoping pipeline: builds the relevance index, runs ranking with pseudo-relevance
///     feedback, multi-query fusion, the distributional thesaurus, member-level retrieval, and the git churn
///     prior, then promotes seeds and expands the dependency graph. Extracted from
///     <see cref="FusionOrchestrator" /> so the query path is testable in isolation; behavior is unchanged.
/// </summary>
public sealed class QueryScopingPipeline
{
    // The candidate pool is widened to this multiple of the seed count when the git churn pool prior is active,
    // so the prior can promote a file the lexical pass ranked just outside the seeds.
    private const int CandidatePoolWideningFactor = 4;

    // A member is kept verbatim only when its query-overlap score is at least this fraction of the file's best
    // member. The floor separates the members the query is genuinely about from siblings that share the type.
    private const double MemberSelectionRatio = 0.4;

    // A query-term hit in a member's name counts for more than one in its body, mirroring the symbol-field boost.
    private const double MemberSymbolWeight = 4.0;

    // Compound PascalCase identifiers (two or more humps): the strongest code signal in a query.
    private static readonly System.Text.RegularExpressions.Regex IdentifierTokenRegex =
        new(@"\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]*)+\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly CapabilityRegistry<IDependencyExtractor> _dependencyExtractors;
    private readonly CapabilityRegistry<ITypeNameLocator> _typeNameLocators;
    private readonly CapabilityRegistry<ISymbolChunkExtractor> _chunkExtractors;
    private readonly Func<IRelevanceIndex> _relevanceIndexFactory;
    private readonly RelevanceIndexCache _relevanceIndexCache;
    private readonly ITokenCostModel _tokenCostModel;
    private readonly Enrichment.IGitStatsProvider _gitStatsProvider;
    private readonly FocusSeedResolver _focusSeedResolver;
    private readonly ILogger _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="QueryScopingPipeline" /> class.
    /// </summary>
    public QueryScopingPipeline(
        CapabilityRegistry<IDependencyExtractor> dependencyExtractors,
        CapabilityRegistry<ITypeNameLocator> typeNameLocators,
        CapabilityRegistry<ISymbolChunkExtractor> chunkExtractors,
        Func<IRelevanceIndex> relevanceIndexFactory,
        RelevanceIndexCache relevanceIndexCache,
        ITokenCostModel tokenCostModel,
        Enrichment.IGitStatsProvider gitStatsProvider,
        FocusSeedResolver focusSeedResolver,
        ILogger? logger = null)
    {
        _dependencyExtractors = dependencyExtractors;
        _typeNameLocators = typeNameLocators;
        _chunkExtractors = chunkExtractors;
        _relevanceIndexFactory = relevanceIndexFactory;
        _relevanceIndexCache = relevanceIndexCache;
        _tokenCostModel = tokenCostModel;
        _gitStatsProvider = gitStatsProvider;
        _focusSeedResolver = focusSeedResolver;
        _logger = logger ?? NullLogger<QueryScopingPipeline>.Instance;
    }

    /// <summary>
    ///     Scopes the file set by query using the supplied dependency graph and proximity edges.
    /// </summary>
    /// <param name="request">The fusion request.</param>
    /// <param name="files">Candidate source files.</param>
    /// <param name="parallelism">Maximum degree of parallelism.</param>
    /// <param name="index">Optional persistent analysis index.</param>
    /// <param name="fuseStore">Optional key-value store for relevance postings.</param>
    /// <param name="contentProvider">Source content provider.</param>
    /// <param name="experimental">Experimental scoping options.</param>
    /// <param name="graph">Pre-built dependency graph.</param>
    /// <param name="proximity">Proximity adjacency and expansion weight.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The scoped file set with scores and provenance.</returns>
    public async Task<FilteredFileSet> ScopeAsync(
        FusionRequest request,
        IReadOnlyList<SourceFile> files,
        int parallelism,
        Indexing.IAnalysisIndex? index,
        IKeyValueStore? fuseStore,
        ISourceContentProvider contentProvider,
        ExperimentalOptions experimental,
        DependencyGraph graph,
        (IReadOnlyDictionary<string, IReadOnlyList<string>>? Edges, double Weight) proximity,
        CancellationToken cancellationToken)
    {
        // File selection ranks at FILE granularity (whole-file BM25F), which preserves recall: a query term
        // anywhere in a file contributes with proper length normalization, so files whose match is spread
        // across members are not penalized. Member-level granularity is applied only to emission (the thin
        // skeleton below), where it improves precision without changing which files are included.
        var documents = new Dictionary<string, IndexedDocument>(StringComparer.OrdinalIgnoreCase);
        var fileContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Accumulate a content signature over (path, content) of every indexed file. The index is a pure
        // function of these (symbols are derived from content deterministically), so the signature keys the
        // process-lifetime index cache: a warm query on an unchanged tree reuses the built index. Files are
        // collected in a stable order, so the order-dependent mix is deterministic.
        // Seed the signature with the comment-field flag so a warm cache built with one setting is not reused by
        // the other (the index is otherwise a pure function of path and content; this flag changes its fields).
        var indexSignature = experimental.FieldedComments ? 1UL : 0UL;

        // Q1: build the per-file documents in parallel (read content, derive declared symbols, extract comments),
        // since each file is independent and the analysis index and content provider are safe for the concurrent
        // access the dependency-graph build already performs. The results are then folded into the dictionaries
        // and the cache signature in the original file order, so the index and its cache key stay byte-identical
        // to the sequential build (the parallelism is a latency win, not a behavior change).
        var built = new System.Collections.Concurrent.ConcurrentDictionary<string, BuiltDocument>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(
            files,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = parallelism },
            async (file, ct) =>
            {
                var content = await contentProvider.GetContentAsync(file, ct);
                var locator = _typeNameLocators.TryResolve(file.Extension);
                var extractor = _dependencyExtractors.TryResolve(file.Extension);

                // Reuse the persistent index for the symbol field so the graph build later in this run also hits.
                var symbols = extractor is not null
                    ? DependencyGraphBuilder.Analyze(content, extractor, locator, index).DeclaredSymbols
                    : locator?.ExtractDefinedSymbols(content);

                // Q2: index comments as their own weighted field only when the lever is on, so the default
                // ranking is byte-identical. Comment extraction is lexical and cheap (a single regex pass).
                var comments = experimental.FieldedComments ? CommentExtractor.Extract(content) : null;

                built[file.NormalizedRelativePath] = new BuiltDocument(content, symbols, comments);
            });

        foreach (var file in files)
        {
            if (!built.TryGetValue(file.NormalizedRelativePath, out var doc))
                continue;

            documents[file.NormalizedRelativePath] = new IndexedDocument(doc.Content, file.NormalizedRelativePath, doc.Symbols, doc.Comments);
            fileContents[file.NormalizedRelativePath] = doc.Content;
            indexSignature = MixSignature(indexSignature, file.NormalizedRelativePath, doc.Content);
        }

        // Reuse a built index across queries on an unchanged tree (item 24): the index rebuilds its
        // document-frequency and length statistics otherwise, which is the dominant warm-call cost once body
        // tokenization is cached. A built index is read-only, so sharing the cached instance across concurrent
        // queries is safe. On a miss, build a fresh per-run index (no cross-run state) and index it; when the
        // persistent index is enabled, body tokenization is cached on disk by content hash as well.
        var relevanceIndex = _relevanceIndexCache.GetOrBuild(indexSignature, () =>
        {
            var freshIndex = _relevanceIndexFactory();
            var postingsStore = request.UsePersistentIndex && fuseStore is not null
                ? new Indexing.SqliteRelevancePostingsStore(fuseStore)
                : null;
            freshIndex.Index(documents, postingsStore);
            return freshIndex;
        });
        // Rank a candidate pool: wider than the seed set so a reranking stage has room to reorder. The
        // lexical default keeps the pool equal to the seed count, so behavior is unchanged.
        var candidateTopK = request.Query!.ResolvedCandidateTopK;
        var seedTopK = request.Query.ResolvedSeedTopK;
        // The git churn pool prior only changes the seed set when the pool is wider than the seed count, since it
        // chooses which candidates become seeds. Widen the pool to several times the seed count when it is active,
        // so the prior can promote a file the lexical pass ranked just outside the seeds.
        var poolPriorActive = experimental.GitChurnWeight > 0;
        if (poolPriorActive)
            candidateTopK = Math.Max(candidateTopK, seedTopK * CandidatePoolWideningFactor);
        // Item 12 (rules-based, no model): when on, emphasize the query's compound PascalCase identifiers via
        // the weighted-terms ranking; otherwise rank the raw query unchanged (the default no-op floor).
        var ranked = experimental.HeuristicQueryRewrite
            ? relevanceIndex.RankScored(HeuristicQueryRewriter.Rewrite(request.Query.Query), candidateTopK)
            : relevanceIndex.RankScored(request.Query.Query, candidateTopK);

        if (ranked.Count == 0)
        {
            throw new FusionValidationException(
                $"Query '{request.Query.Query}' matched no collected files.");
        }

        if (experimental.MultiQueryFusion)
        {
            // Multi-query fusion: rank a few diverse query variants and combine with Reciprocal Rank Fusion, so
            // a file several variants agree on outranks one variant's lone top hit. Variants: the raw query, an
            // identifier-only subset (the compound type-like tokens, which carry the strongest code signal), and
            // the pseudo-relevance-expanded query. RRF needs no score calibration across the variants.
            var variants = new List<IReadOnlyList<RankedFile>> { ranked };

            var identifierTerms = ExtractIdentifierTerms(request.Query.Query);
            if (identifierTerms.Count > 0)
            {
                var identifierRanked = relevanceIndex.RankScored(identifierTerms, request.Query.TopFiles);
                if (identifierRanked.Count > 0)
                    variants.Add(identifierRanked);
            }

            if (experimental.QueryExpansion)
            {
                var expandedQuery = PseudoRelevanceExpander.Expand(
                    request.Query.Query, ranked, documents, new QueryExpansionOptions(ExpansionWeight: experimental.ExpansionWeight), relevanceIndex.InverseDocumentFrequency);
                var prfRanked = relevanceIndex.RankScored(expandedQuery, request.Query.TopFiles);
                if (prfRanked.Count > 0)
                    variants.Add(prfRanked);
            }

            var fused = RankFusion.Fuse(variants, request.Query.TopFiles);
            if (fused.Count > 0)
                ranked = fused;
        }
        else if (experimental.QueryExpansion)
        {
            // Pseudo-relevance feedback: rewrite a sparse query in the codebase's own vocabulary by blending in
            // recurring declared-symbol terms from the first pass's top files, then re-rank. Conservative by
            // construction (symbol field only, multi-doc terms only, reduced weight); a no-op when disabled or
            // when no term qualifies, so the seed set then equals the single-pass ordering.
            var expandedQuery = PseudoRelevanceExpander.Expand(
                request.Query.Query, ranked, documents, new QueryExpansionOptions(ExpansionWeight: experimental.ExpansionWeight), relevanceIndex.InverseDocumentFrequency);
            var reranked = relevanceIndex.RankScored(expandedQuery, request.Query.TopFiles);
            // Merge rather than replace: expansion adds the files it surfaces but never drops a first-pass
            // seed, so a misfiring expansion cannot lower recall below the single-pass result.
            if (reranked.Count > 0)
                ranked = PseudoRelevanceExpander.MergePreservingSeeds(ranked, reranked);
        }

        // Distributional thesaurus (Q4): expand the query with corpus identifiers that co-occur with its terms
        // (PMI over declared symbols), then re-rank and merge preserving seeds. This bridges to a related
        // vocabulary the pseudo-relevance feedback set never contained, fully lexically. A no-op when no
        // association clears the gates, so it cannot lower recall below the pre-expansion result.
        if (experimental.DistributionalThesaurus)
        {
            var queryTerms = RelevanceTokenizer.Tokenize(request.Query.Query);
            if (queryTerms.Count > 0)
            {
                var documentSymbolTerms = documents.Values
                    .Select(d => (IReadOnlySet<string>)new HashSet<string>(
                        TokenizeSymbolsForThesaurus(d.Symbols), StringComparer.Ordinal))
                    .ToList();

                var associates = DistributionalThesaurus.Expand(queryTerms, documentSymbolTerms);
                if (associates.Count > 0)
                {
                    var expanded = new Dictionary<string, double>(StringComparer.Ordinal);
                    foreach (var term in queryTerms)
                        expanded[term] = 1.0;
                    foreach (var (term, weight) in associates)
                        expanded.TryAdd(term, weight);

                    var thesaurusRanked = relevanceIndex.RankScored(expanded, request.Query.TopFiles);
                    if (thesaurusRanked.Count > 0)
                        ranked = PseudoRelevanceExpander.MergePreservingSeeds(ranked, thesaurusRanked);
                }
            }
        }

        // Member-level retrieval (Q5): index each declared member as its own document, roll the per-member
        // scores up to a file score (best member wins), and add any file the member pass surfaces that the
        // file-granular pass missed, as an extra seed. This reaches a file whose match is concentrated in one
        // member of an otherwise large file, which whole-file length normalization dilutes. Member-rollup
        // scores come from a separate index on a different scale, so the additions are placed below the
        // file-granular floor rather than interleaved by raw score, and the seed count is widened to admit them,
        // so the pass only adds files and never drops a first-pass seed.
        if (experimental.MemberLevelRetrieval)
        {
            // Rank a wider member pool than the seed count, so a file the member pass surfaces is captured even
            // when higher-density members of already-seeded files rank above it; then admit at most TopFiles of
            // the files not already present, so the extra seeds stay bounded.
            var memberRanked = RankByMembers(request.Query.Query, files, fileContents, request.Query.TopFiles * 4);
            var present = new HashSet<string>(ranked.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
            var additions = memberRanked
                .Where(r => !present.Contains(r.Path))
                .Take(request.Query.TopFiles)
                .ToList();
            if (additions.Count > 0)
            {
                var floor = ranked.Count > 0 ? ranked.Min(r => r.Score) : 1.0;
                var combined = ranked.ToList();
                foreach (var addition in additions)
                {
                    floor *= 0.999; // strictly below the file-granular floor, preserving member order
                    combined.Add(addition with { Score = floor });
                }

                ranked = combined;
                seedTopK += additions.Count; // admit the surfaced files as extra seeds
            }
        }

        // Git churn prior (Q6): nudge a recently and frequently changed candidate up, since work clusters where
        // code recently changed. A production-routing lever, off unless GitChurnWeight > 0. The pinned benchmark
        // cannot validate it: its worktrees are historical PR-head checkouts, so churn-from-now is uniformly
        // empty (a no-op), and a commit-date-relative churn would leak (the changed files are the most recently
        // changed by construction). It therefore stays off by default and is not a benchmark lever.
        if (experimental.GitChurnWeight > 0)
        {
            ranked = await ApplyGitChurnPriorAsync(
                ranked, request.Collection.SourceDirectory, experimental.GitChurnWeight, cancellationToken);
        }

        // Promote the top seedTopK of the (possibly reordered) candidate pool to expansion seeds. With the
        // lexical default the pool and seed count are equal, so every candidate is a seed as before.
        var seedRanked = ranked.Count > seedTopK ? ranked.Take(seedTopK).ToList() : ranked;
        var seedScores = seedRanked.ToDictionary(r => r.Path, r => r.Score, StringComparer.OrdinalIgnoreCase);

        // Symbol-level packing (precision only): pick, per matched file, the members the query is about so
        // emission can keep them in full and collapse the rest to signatures. This never changes file
        // selection, so recall is identical to the file-granular path.
        var selectedMembers = SelectQueryMembers(ranked, fileContents, request.Query.Query);

        // Budget-aware expansion (item 4): when a token ceiling is set, gate neighbour admission by an
        // estimated reduced cost so the graph stops admitting once the budget is spent, instead of admitting
        // the whole neighbourhood and leaving the packer to cut it (which wastes reduction on files that never
        // emit, and can lose a truth file in the knapsack). Costs are estimated at the level each file will be
        // emitted at: a seed at the request level, a neighbour at the skeleton level when tiered emission is on
        // (matching BuildTieredLevelResolver), so a cheap skeletonized neighbour is not rejected as a full body.
        int? expansionBudget = null;
        IReadOnlyDictionary<string, int>? expansionCosts = null;
        if (experimental.BudgetAwareExpansion && request.Emission.MaxTokens is { } maxTokens && maxTokens > 0)
        {
            expansionBudget = maxTokens;
            var seedLevel = request.Reduction.Level;
            var neighbourLevel = experimental.TieredEmission
                ? Fuse.Plugins.Abstractions.Options.ReductionLevel.Skeleton
                : seedLevel;
            var costs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                if (!fileContents.TryGetValue(file.NormalizedRelativePath, out var content))
                    continue;

                var level = seedScores.ContainsKey(file.NormalizedRelativePath) ? seedLevel : neighbourLevel;
                costs[file.NormalizedRelativePath] = _tokenCostModel.EstimateReducedTokens(content, file.Extension, level);
            }

            expansionCosts = costs;
        }

        // Query seeds are already content-matched; expand forward to their dependencies for context, but do
        // not follow dependents, which would broaden the set with files that merely use a matched type. A
        // measured A/B over the pinned corpus confirmed this: enabling reverse hops dropped query recall 51 to
        // 45 percent at the headline budget (Newtonsoft.Json 30 to 13), as common-type dependents displaced
        // the real targets under the token budget.
        var options = new ExpansionOptions(
            request.Query.Depth,
            FollowReferences: true,
            FollowDependents: false,
            HopDecay: experimental.HopDecay,
            TokenBudget: expansionBudget,
            TokenCosts: expansionCosts,
            Centrality: GraphCentrality.Compute(graph),
            CentralityWeight: experimental.CentralityWeight,
            ProximityEdges: proximity.Edges,
            ProximityWeight: proximity.Weight);

        var expansion = _focusSeedResolver.Expand(graph, seedScores, options);
        var filtered = files.Where(f => expansion.IncludedPaths.Contains(f.NormalizedRelativePath)).ToArray();
        return new FilteredFileSet(
            filtered, expansion.ProvenanceChains, expansion.Scores, SelectedMembers: selectedMembers);
    }

    // Member-level retrieval (Q5). Indexes each declared member of each file as its own document, ranks the
    // query over members, and rolls the per-member scores up to a file score (the file's best member). Returns
    // the top files by member score, to be merged with the file-granular ranking. A fresh per-call index over
    // member chunks; empty when no file has a chunk extractor or no member matches.
    private IReadOnlyList<RankedFile> RankByMembers(
        string query,
        IReadOnlyList<SourceFile> files,
        IReadOnlyDictionary<string, string> fileContents,
        int topFiles)
    {
        var chunkDocuments = new Dictionary<string, IndexedDocument>(StringComparer.Ordinal);
        var chunkToFile = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var extractor = _chunkExtractors.TryResolve(file.Extension);
            if (extractor is null || !fileContents.TryGetValue(file.NormalizedRelativePath, out var content))
                continue;

            var chunks = extractor.ExtractChunks(content);
            for (var i = 0; i < chunks.Count; i++)
            {
                var key = $"{file.NormalizedRelativePath}{i}";
                chunkDocuments[key] = new IndexedDocument(chunks[i].Content, key, [chunks[i].SymbolName]);
                chunkToFile[key] = file.NormalizedRelativePath;
            }
        }

        if (chunkDocuments.Count == 0)
            return [];

        var chunkIndex = _relevanceIndexFactory();
        chunkIndex.Index(chunkDocuments);
        var chunkRanked = chunkIndex.RankScored(query, chunkDocuments.Count);

        // Roll up: each file takes its best-scoring member.
        var fileScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in chunkRanked)
        {
            var path = chunkToFile[chunk.Path];
            if (!fileScores.TryGetValue(path, out var best) || chunk.Score > best)
                fileScores[path] = chunk.Score;
        }

        return fileScores
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(topFiles)
            .Select(kv => new RankedFile(kv.Key, kv.Value))
            .ToList();
    }

    // One file's parallel-built relevance inputs (Q1), folded into the index in stable file order afterward.
    private readonly record struct BuiltDocument(string Content, IReadOnlyList<string>? Symbols, string? Comments);

    // Folds one file's path and content into the running index signature with an FNV-style mix over their
    // 64-bit hashes. Order-dependent, but files are collected in a stable order, so the signature is
    // deterministic for a given tree and changes whenever any file's path or content changes.
    private static ulong MixSignature(ulong accumulator, string path, string content)
    {
        const ulong prime = 1099511628211UL;
        var pathHash = System.IO.Hashing.XxHash64.HashToUInt64(System.Text.Encoding.UTF8.GetBytes(path));
        var contentHash = System.IO.Hashing.XxHash64.HashToUInt64(System.Text.Encoding.UTF8.GetBytes(content));
        accumulator = (accumulator ^ pathHash) * prime;
        accumulator = (accumulator ^ contentHash) * prime;
        return accumulator;
    }

    // Tokenizes a file's declared symbols into the term set the distributional thesaurus co-occurs over, using
    // the same tokenizer as the index so the terms match the query terms.
    private static IEnumerable<string> TokenizeSymbolsForThesaurus(IReadOnlyList<string>? symbols)
    {
        if (symbols is null)
            yield break;

        foreach (var symbol in symbols)
            foreach (var term in RelevanceTokenizer.Tokenize(symbol))
                yield return term;
    }

    // Extracts the compound-identifier tokens from a query as a weighted-term set for an identifier-only ranking
    // variant. Empty when the query names no compound identifier, in which case the variant is skipped.
    private static Dictionary<string, double> ExtractIdentifierTerms(string query)
    {
        var terms = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match match in IdentifierTokenRegex.Matches(query))
            foreach (var term in RelevanceTokenizer.Tokenize(match.Value))
                terms[term] = 1.0;

        return terms;
    }

    // For each query-matched file, scores its members by query-term overlap and returns the qualified names of
    // the members the query is about (those scoring near the file's best). Files with no chunk extractor, no
    // chunks, or no matching member are omitted, so they keep their full reduced content; only files with a
    // clear member match are trimmed to a thin skeleton. This is emission-only and never affects file
    // selection, so query recall is identical to the file-granular path.
    private Dictionary<string, IReadOnlySet<string>> SelectQueryMembers(
        IReadOnlyList<RankedFile> ranked,
        IReadOnlyDictionary<string, string> fileContents,
        string query)
    {
        var queryTerms = RelevanceTokenizer.Tokenize(query)
            .ToHashSet(StringComparer.Ordinal);
        var selected = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        if (queryTerms.Count == 0)
            return selected;

        foreach (var candidate in ranked)
        {
            if (!fileContents.TryGetValue(candidate.Path, out var content))
                continue;

            var extractor = _chunkExtractors.TryResolve(Path.GetExtension(candidate.Path));
            var chunks = extractor?.ExtractChunks(content);
            if (chunks is null || chunks.Count == 0)
                continue;

            var best = 0.0;
            var scores = new List<(string Qualified, double Score)>(chunks.Count);
            foreach (var chunk in chunks)
            {
                var score = MemberQueryScore(chunk, queryTerms);
                if (score > 0)
                {
                    scores.Add((chunk.Identity, score));
                    if (score > best)
                        best = score;
                }
            }

            if (best <= 0)
                continue; // No member matched the query: keep the whole (reduced) file.

            var floor = best * MemberSelectionRatio;
            var kept = scores
                .Where(s => s.Score >= floor)
                .Select(s => s.Qualified)
                .ToHashSet(StringComparer.Ordinal);
            if (kept.Count > 0)
                selected[candidate.Path] = kept;
        }

        return selected;
    }

    private static double MemberQueryScore(
        SymbolChunk chunk,
        IReadOnlySet<string> queryTerms)
    {
        var score = 0.0;
        foreach (var term in RelevanceTokenizer.Tokenize(chunk.SymbolName))
        {
            if (queryTerms.Contains(term))
                score += MemberSymbolWeight;
        }

        foreach (var term in RelevanceTokenizer.Tokenize(chunk.Content))
        {
            if (queryTerms.Contains(term))
                score += 1.0;
        }

        return score;
    }

    // Git churn prior (Q6). Multiplies each candidate's score by (1 + weight * normalized recent commit count),
    // so a frequently and recently changed file ranks slightly higher. Normalized by the pool's maximum churn
    // and held to a conservative weight, so it tilts ties rather than overruling a strong lexical match. A
    // no-op when git is unavailable or no candidate has recent churn (for example a historical checkout).
    private async Task<IReadOnlyList<RankedFile>> ApplyGitChurnPriorAsync(
        IReadOnlyList<RankedFile> ranked,
        string sourceDirectory,
        double weight,
        CancellationToken cancellationToken)
    {
        var paths = ranked.Select(r => r.Path).ToList();
        var stats = await _gitStatsProvider.GetStatsAsync(sourceDirectory, paths, cancellationToken);
        if (!stats.IsAvailable || stats.StatsByPath.Count == 0)
            return ranked;

        var maxChurn = stats.StatsByPath.Values.Max(s => s.CommitCount);
        if (maxChurn <= 0)
            return ranked;

        var boosted = ranked.Select(r =>
        {
            var churn = stats.StatsByPath.TryGetValue(r.Path, out var s) ? s.CommitCount : 0;
            var churnNorm = (double)churn / maxChurn;
            return r with { Score = r.Score * (1 + weight * churnNorm) };
        });

        return boosted
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
