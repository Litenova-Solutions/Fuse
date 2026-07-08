namespace Fuse.Workspace;

/// <summary>
///     Classifies a covering-test run's coverage (T1): which of the requested covering test types produced no
///     verdict, so they are reported not-runnable by name rather than silently counted green. A covering type with
///     no result in the run either failed at test collection or has no runnable test, and either way the run did
///     not verify it.
/// </summary>
public static class CoveringRunAnalysis
{
    /// <summary>
    ///     Returns the covering test types that produced no verdict in the run (requested by the filter, absent
    ///     from the results), so a caller reports them not-runnable rather than as passed.
    /// </summary>
    /// <param name="coveringTypeNames">The covering test type names the run was filtered to.</param>
    /// <param name="verdicts">The per-test verdicts the run produced.</param>
    /// <returns>The covering type names with no matching verdict, in input order, deduplicated.</returns>
    public static IReadOnlyList<string> NotRunnableTypes(
        IReadOnlyList<string> coveringTypeNames, IReadOnlyList<TestVerdict> verdicts)
    {
        var ranNames = verdicts.Select(v => v.Name).ToList();
        return coveringTypeNames
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Where(type => !ranNames.Any(name => BelongsToType(name, type)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    // A verdict belongs to a covering type when its fully qualified name contains the type name, matching the
    // contains-filter the run was scoped with (so a method Ns.OrderTests.Case1 counts for the type Ns.OrderTests).
    private static bool BelongsToType(string testName, string typeName) =>
        testName.Contains(typeName, StringComparison.Ordinal);
}
