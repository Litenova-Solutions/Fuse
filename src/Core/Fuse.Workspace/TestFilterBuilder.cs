namespace Fuse.Workspace;

/// <summary>
///     Builds the test-runner filter expression that selects exactly the covering subset (T1): a
///     <c>FullyQualifiedName=...</c> disjunction understood by both <c>dotnet test --filter</c> and
///     <c>dotnet vstest --TestCaseFilter</c>, so only the tests that reach the changed symbols run rather than the
///     whole suite.
/// </summary>
/// <remarks>
///     A fully qualified name containing a filter operator character (<c>(</c>, <c>)</c>, <c>&amp;</c>, <c>|</c>,
///     <c>=</c>, <c>!</c>, <c>~</c>) is escaped with a backslash so a parameterized test name does not break the
///     expression. An empty set yields an empty string; the caller treats that as "nothing to run" rather than
///     "run everything" (an empty filter would run the whole suite).
/// </remarks>
public static class TestFilterBuilder
{
    private static readonly char[] OperatorCharacters = ['(', ')', '&', '|', '=', '!', '~', '\\'];

    /// <summary>
    ///     Builds the filter expression selecting the given fully qualified test names.
    /// </summary>
    /// <param name="fullyQualifiedNames">The fully qualified test method names to select.</param>
    /// <returns>
    ///     A <c>FullyQualifiedName=A|FullyQualifiedName=B</c> expression, or an empty string when no names are
    ///     given (which the caller must treat as "run nothing", never as an unfiltered run).
    /// </returns>
    public static string Build(IEnumerable<string> fullyQualifiedNames)
    {
        var clauses = fullyQualifiedNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => "FullyQualifiedName=" + Escape(name.Trim()))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return string.Join("|", clauses);
    }

    private static string Escape(string name)
    {
        if (name.IndexOfAny(OperatorCharacters) < 0)
            return name;

        var builder = new System.Text.StringBuilder(name.Length + 8);
        foreach (var character in name)
        {
            if (Array.IndexOf(OperatorCharacters, character) >= 0)
                builder.Append('\\');
            builder.Append(character);
        }

        return builder.ToString();
    }
}
