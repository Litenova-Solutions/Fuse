using Fuse.Indexing;

namespace Fuse.Retrieval;

/// <summary>
///     Produces candidate files and symbols for a localization request from one signal (exact resolution,
///     full-text, path, or diff).
/// </summary>
public interface ICandidateGenerator
{
    /// <summary>
    ///     Generates candidates for a request.
    /// </summary>
    /// <param name="request">The localization request.</param>
    /// <param name="cancellationToken">A token to cancel generation.</param>
    /// <returns>The candidates this generator found (possibly empty).</returns>
    Task<IReadOnlyList<CandidateNode>> GenerateAsync(LocalizationRequest request, CancellationToken cancellationToken);
}

/// <summary>
///     Runs every registered candidate generator and concatenates their output.
/// </summary>
public sealed class CandidateGenerator
{
    private readonly IReadOnlyList<ICandidateGenerator> _generators;

    /// <summary>
    ///     Initializes a new instance of the <see cref="CandidateGenerator" /> class.
    /// </summary>
    /// <param name="generators">The generators to run.</param>
    public CandidateGenerator(IEnumerable<ICandidateGenerator> generators) => _generators = generators.ToList();

    /// <summary>
    ///     Creates a generator wired with the default set (exact, FTS, path, diff) over a store.
    /// </summary>
    /// <param name="store">The index store to query.</param>
    /// <returns>A generator with the standard candidate sources.</returns>
    public static CandidateGenerator CreateDefault(IWorkspaceIndexStore store) =>
        new(
        [
            new ExactCandidateGenerator(store),
            new FtsCandidateGenerator(store),
            new PathCandidateGenerator(store),
            new DiffCandidateGenerator(),
        ]);

    /// <summary>
    ///     Generates candidates from all sources.
    /// </summary>
    /// <param name="request">The localization request.</param>
    /// <param name="cancellationToken">A token to cancel generation.</param>
    /// <returns>The concatenated candidates from every generator.</returns>
    public async Task<IReadOnlyList<CandidateNode>> GenerateAsync(LocalizationRequest request, CancellationToken cancellationToken)
    {
        var candidates = new List<CandidateNode>();
        foreach (var generator in _generators)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates.AddRange(await generator.GenerateAsync(request, cancellationToken));
        }

        return candidates;
    }
}

/// <summary>
///     Generates candidates by exact semantic resolution (service, request, route, config, symbol).
/// </summary>
public sealed class ExactCandidateGenerator : ICandidateGenerator
{
    private readonly SemanticResolver _resolver;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ExactCandidateGenerator" /> class.
    /// </summary>
    /// <param name="store">The index store to query.</param>
    public ExactCandidateGenerator(IWorkspaceIndexStore store) => _resolver = new SemanticResolver(store);

    /// <inheritdoc />
    public async Task<IReadOnlyList<CandidateNode>> GenerateAsync(LocalizationRequest request, CancellationToken cancellationToken)
    {
        var candidates = new List<CandidateNode>();

        if (!string.IsNullOrWhiteSpace(request.Service))
            Add(candidates, await _resolver.ResolveServiceAsync(request.Service, cancellationToken), CandidateSource.ServiceExact);
        if (!string.IsNullOrWhiteSpace(request.Request))
            Add(candidates, await _resolver.ResolveRequestAsync(request.Request, cancellationToken), CandidateSource.RequestExact);
        if (!string.IsNullOrWhiteSpace(request.Route))
            Add(candidates, await _resolver.ResolveRouteAsync(request.Route, cancellationToken), CandidateSource.RouteExact);
        if (!string.IsNullOrWhiteSpace(request.ConfigSection))
            Add(candidates, await _resolver.ResolveConfigAsync(request.ConfigSection, cancellationToken), CandidateSource.ConfigExact);
        if (!string.IsNullOrWhiteSpace(request.Focus))
            Add(candidates, await _resolver.ResolveSymbolAsync(request.Focus, cancellationToken), CandidateSource.SymbolExact);

        return candidates;
    }

    private static void Add(List<CandidateNode> candidates, ResolveResult result, CandidateSource source)
    {
        foreach (var match in result.Matches)
        {
            if (match.FilePath is null)
                continue;

            candidates.Add(new CandidateNode(
                NodeId: match.NodeId,
                FilePath: match.FilePath,
                Kind: match.Kind,
                BaseScore: CandidateSourceWeights.Weight(source),
                Source: source,
                Reasons: [$"{source}: {result.Query} -> {match.DisplayName} ({match.Relation})"],
                TokenEstimate: 0));
        }
    }
}

/// <summary>
///     Generates candidates from full-text search over indexed chunks.
/// </summary>
public sealed class FtsCandidateGenerator : ICandidateGenerator
{
    private readonly IWorkspaceIndexStore _store;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FtsCandidateGenerator" /> class.
    /// </summary>
    /// <param name="store">The index store to query.</param>
    public FtsCandidateGenerator(IWorkspaceIndexStore store) => _store = store;

    /// <inheritdoc />
    public async Task<IReadOnlyList<CandidateNode>> GenerateAsync(LocalizationRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return [];

        var hits = await _store.SearchAsync(new SearchQuery(request.Query, request.MaxCandidates), cancellationToken);
        var candidates = new List<CandidateNode>();
        foreach (var hit in hits)
        {
            // A hit whose name matches a query token is a stronger (symbol) signal than a body-only match.
            var source = hit.Name is not null && ContainsToken(request.Query, hit.Name)
                ? CandidateSource.FtsSymbol
                : CandidateSource.FtsBody;

            candidates.Add(new CandidateNode(
                NodeId: string.Empty,
                FilePath: hit.FilePath,
                Kind: hit.Kind,
                BaseScore: CandidateSourceWeights.Weight(source),
                Source: source,
                Reasons: [$"FTS match: {hit.Name ?? hit.Kind} ({hit.FilePath}:{hit.StartLine})"],
                TokenEstimate: 0));
        }

        return candidates;
    }

    private static bool ContainsToken(string query, string name) =>
        query.Split([' ', '\t', '\n', '.', '(', ')', ',', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
///     Generates candidates by matching the query against indexed file paths.
/// </summary>
public sealed class PathCandidateGenerator : ICandidateGenerator
{
    private readonly IWorkspaceIndexStore _store;

    /// <summary>
    ///     Initializes a new instance of the <see cref="PathCandidateGenerator" /> class.
    /// </summary>
    /// <param name="store">The index store to query.</param>
    public PathCandidateGenerator(IWorkspaceIndexStore store) => _store = store;

    /// <inheritdoc />
    public async Task<IReadOnlyList<CandidateNode>> GenerateAsync(LocalizationRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return [];

        var candidates = new List<CandidateNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in Tokens(request.Query))
        {
            foreach (var file in await _store.FindFilesByPathAsync(token, request.MaxCandidates, cancellationToken))
            {
                if (!seen.Add(file.NormalizedPath))
                    continue;

                candidates.Add(new CandidateNode(
                    NodeId: string.Empty,
                    FilePath: file.NormalizedPath,
                    Kind: "file",
                    BaseScore: CandidateSourceWeights.Weight(CandidateSource.FtsPath),
                    Source: CandidateSource.FtsPath,
                    Reasons: [$"path match: {token}"],
                    TokenEstimate: 0));
            }
        }

        return candidates;
    }

    // Only path-like tokens (3+ chars, alphanumeric) are matched, so common short words do not match every file.
    private static IEnumerable<string> Tokens(string query) =>
        query.Split([' ', '\t', '\n', '.', '(', ')', ',', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 3 && token.All(char.IsLetterOrDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
///     Generates must-keep candidates from explicitly selected paths (changed or pinned files).
/// </summary>
/// <remarks>
///     Git base resolution (<c>ChangedSince</c>) is wired into review in a later phase; this generator handles
///     the explicit <see cref="LocalizationRequest.SelectedPaths" />.
/// </remarks>
public sealed class DiffCandidateGenerator : ICandidateGenerator
{
    /// <inheritdoc />
    public Task<IReadOnlyList<CandidateNode>> GenerateAsync(LocalizationRequest request, CancellationToken cancellationToken)
    {
        if (request.SelectedPaths is not { Count: > 0 } paths)
            return Task.FromResult<IReadOnlyList<CandidateNode>>([]);

        var candidates = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Replace('\\', '/'))
            .Select(p => new CandidateNode(
                NodeId: string.Empty,
                FilePath: p,
                Kind: "file",
                BaseScore: CandidateSourceWeights.Weight(CandidateSource.DiffChangedFile),
                Source: CandidateSource.DiffChangedFile,
                Reasons: ["selected/changed file"],
                TokenEstimate: 0))
            .ToList();

        return Task.FromResult<IReadOnlyList<CandidateNode>>(candidates);
    }
}
