using System.Text.RegularExpressions;

namespace Fuse.Analysis.Search;

/// <summary>
///     BM25 inverted index over tokenized file content and identifiers.
/// </summary>
/// <remarks>
///     Ranking is a lexical, best-effort relevance heuristic: it matches query terms against tokenized
///     identifiers (including camelCase and snake_case sub-words) and has no semantic understanding, so it
///     can surface incidental term overlaps and miss conceptually relevant files that share no vocabulary.
///     <see cref="Index" /> must be called before <see cref="Rank" />; it is not thread-safe and rebuilds
///     all state on each call.
/// </remarks>
/// <seealso cref="IRelevanceIndex" />
public sealed class Bm25RelevanceIndex : IRelevanceIndex
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private static readonly Regex IdentifierSplitter = new(
        @"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|_+",
        RegexOptions.Compiled);

    private readonly Dictionary<string, Dictionary<string, int>> _termFrequencies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _documentLengths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _documentFrequencies = new(StringComparer.OrdinalIgnoreCase);
    private double _averageDocumentLength;

    /// <inheritdoc />
    public void Index(IReadOnlyDictionary<string, string> fileContents)
    {
        _termFrequencies.Clear();
        _documentLengths.Clear();
        _documentFrequencies.Clear();
        _averageDocumentLength = 0;

        if (fileContents.Count == 0)
            return;

        foreach (var (path, content) in fileContents)
        {
            var terms = Tokenize(content);
            _documentLengths[path] = terms.Count;

            var termCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var term in terms)
            {
                termCounts.TryGetValue(term, out var count);
                termCounts[term] = count + 1;
            }

            _termFrequencies[path] = termCounts;

            foreach (var term in termCounts.Keys)
            {
                _documentFrequencies.TryGetValue(term, out var df);
                _documentFrequencies[term] = df + 1;
            }
        }

        _averageDocumentLength = _documentLengths.Values.Average();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Rank(string query, int topN)
    {
        if (_termFrequencies.Count == 0 || string.IsNullOrWhiteSpace(query))
            return [];

        var queryTerms = Tokenize(query).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (queryTerms.Length == 0)
            return [];

        var totalDocuments = _termFrequencies.Count;
        var scores = new List<(string Path, double Score)>();

        foreach (var (path, termCounts) in _termFrequencies)
        {
            var docLength = _documentLengths[path];
            var score = 0.0;

            foreach (var term in queryTerms)
            {
                if (!termCounts.TryGetValue(term, out var termFrequency))
                    continue;

                if (!_documentFrequencies.TryGetValue(term, out var documentFrequency))
                    continue;

                var idf = Math.Log((totalDocuments - documentFrequency + 0.5) / (documentFrequency + 0.5) + 1);
                var numerator = termFrequency * (K1 + 1);
                var denominator = termFrequency + K1 * (1 - B + B * docLength / _averageDocumentLength);
                score += idf * numerator / denominator;
            }

            if (score > 0)
                scores.Add((path, score));
        }

        return scores
            .OrderByDescending(s => s.Score)
            .Take(topN)
            .Select(s => s.Path)
            .ToArray();
    }

    private static List<string> Tokenize(string text)
    {
        var terms = new List<string>();
        foreach (var raw in Regex.Split(text, @"\W+"))
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            terms.Add(raw.ToLowerInvariant());

            foreach (var part in IdentifierSplitter.Split(raw))
            {
                if (string.IsNullOrWhiteSpace(part))
                    continue;

                terms.Add(part.ToLowerInvariant());
            }
        }

        return terms;
    }
}
