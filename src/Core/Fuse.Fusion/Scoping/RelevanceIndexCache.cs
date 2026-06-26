namespace Fuse.Fusion.Scoping;

/// <summary>
///     A process-lifetime cache of one built <see cref="IRelevanceIndex" />, keyed by a content signature of the
///     indexed documents, so a warm scoped query against an unchanged tree reuses the index rather than
///     rebuilding its document-frequency and length statistics every call (item 24).
/// </summary>
/// <remarks>
///     A built index is read-only: <see cref="IRelevanceIndex.RankScored(string, int)" /> only reads its
///     tables, so the cached instance is safe to share across the MCP server's concurrent queries. The cache
///     holds a single entry (the last signature), which suits a server serving one repository; alternating
///     repositories simply rebuild. Correctness rests on the signature: it must cover every document's path and
///     content, since the index is a pure function of those. The build runs outside the lock, so concurrent
///     first-time builds are wasteful but never incorrect (last write wins, and all are equal for one
///     signature).
/// </remarks>
public sealed class RelevanceIndexCache
{
    private readonly object _gate = new();
    private ulong _signature;
    private bool _hasEntry;
    private IRelevanceIndex? _index;

    /// <summary>
    ///     Returns the cached index when its signature matches, otherwise builds one with <paramref name="build" />,
    ///     stores it, and returns it.
    /// </summary>
    /// <param name="signature">A content signature of the documents to be indexed (path and content of every file).</param>
    /// <param name="build">Builds and indexes a fresh index; called only on a cache miss.</param>
    /// <returns>The cached or newly built index for <paramref name="signature" />.</returns>
    public IRelevanceIndex GetOrBuild(ulong signature, Func<IRelevanceIndex> build)
    {
        lock (_gate)
        {
            if (_hasEntry && _signature == signature && _index is not null)
                return _index;
        }

        // Build outside the lock so a slow index build does not serialize unrelated queries. A concurrent build
        // for the same signature produces an equal index, so a last-write-wins store is correct.
        var built = build();

        lock (_gate)
        {
            if (_hasEntry && _signature == signature && _index is not null)
                return _index;

            _signature = signature;
            _index = built;
            _hasEntry = true;
            return built;
        }
    }
}
