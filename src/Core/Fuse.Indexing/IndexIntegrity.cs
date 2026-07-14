namespace Fuse.Indexing;

/// <summary>
///     The result of a cheap index integrity check (R31): whether a warm store satisfies its invariants, and the
///     violations when it does not. A store that fails any invariant must never be reported <c>ready</c>; it is
///     reported rebuilding and repaired via the R23 rebuild path.
/// </summary>
/// <param name="Healthy">Whether the store satisfied every invariant.</param>
/// <param name="Violations">The invariant violations found; empty when <see cref="Healthy" /> is true.</param>
public sealed record IndexIntegrityResult(bool Healthy, IReadOnlyList<string> Violations)
{
    /// <summary>A healthy result with no violations.</summary>
    public static IndexIntegrityResult Ok { get; } = new(true, []);

    /// <summary>A one-line summary suitable for a status or doctor line.</summary>
    /// <returns><c>ok</c> when healthy; otherwise the joined violations.</returns>
    public string Summary() => Healthy ? "ok" : string.Join("; ", Violations);
}

/// <summary>
///     Cheap, state-based index invariants (R31), checked after every build and on open so an internally
///     inconsistent store is never presented as <c>ready</c> and served empty. The 0-chunk, mode="unknown" store
///     from the dogfood is the canonical failure this prevents.
/// </summary>
public static class IndexIntegrity
{
    /// <summary>
    ///     Checks the invariants against a warm store's state. Pure and cheap (no I/O); the state is already read
    ///     for status. Intended for a populated store (a store with zero files is <c>not_indexed</c>, not a
    ///     violation).
    /// </summary>
    /// <param name="state">The workspace index state (schema version, mode, counts, FTS availability).</param>
    /// <returns>The integrity result.</returns>
    /// <remarks>
    ///     Invariants:
    ///     <list type="bullet">
    ///     <item>the schema version is set (a populated store carries its schema version);</item>
    ///     <item>the index mode is set (never <c>null</c> or <c>unknown</c> for a populated store);</item>
    ///     <item>chunks exist when symbols exist on an FTS-available runtime (else search over indexed source is
    ///     empty). An FTS-unavailable runtime legitimately has no chunk index, so zero chunks is not a violation
    ///     there.</item>
    ///     </list>
    /// </remarks>
    public static IndexIntegrityResult Check(WorkspaceIndexState state)
    {
        // A store with no files is cold/not-indexed, not inconsistent; the caller reports not_indexed first.
        if (state.FileCount == 0)
            return IndexIntegrityResult.Ok;

        var violations = new List<string>();

        if (state.SchemaVersion <= 0)
            violations.Add("schema version is not set");

        if (string.IsNullOrWhiteSpace(state.Mode) || string.Equals(state.Mode, "unknown", StringComparison.OrdinalIgnoreCase))
            violations.Add("index mode is not set");

        if (state.FtsAvailable && state.SymbolCount > 0 && state.ChunkCount == 0)
            violations.Add("indexed symbols but no chunks (full-text search over indexed source would be empty)");

        return violations.Count == 0 ? IndexIntegrityResult.Ok : new IndexIntegrityResult(false, violations);
    }
}
