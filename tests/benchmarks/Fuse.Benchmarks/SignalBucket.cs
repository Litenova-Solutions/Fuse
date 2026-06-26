using System.Text.RegularExpressions;

namespace Fuse.Benchmarks;

/// <summary>
///     Classifies a task by the kind of signal its title carries, so that easy identifier-rich tasks
///     do not mask failures on config, dependency-bump, or no-signal tasks (Section 18.5/18.11).
/// </summary>
/// <remarks>
///     The no-signal classification ports the harness <c>Test-PrTitleRelevant</c> rule: a title that
///     points at infrastructure or merge noise cannot locate its own C# change set, so query retrieval
///     cannot solve it and the honest answer is to detect low signal rather than return junk.
/// </remarks>
public static partial class SignalBucket
{
    /// <summary>The bucket for a title with no usable scoping signal (merge noise, empty).</summary>
    public const string NoSignal = "no-signal";

    /// <summary>The bucket for a dependency or version bump.</summary>
    public const string DependencyBump = "dependency-bump";

    /// <summary>The bucket for a CI or build configuration change.</summary>
    public const string ConfigCi = "config-ci";

    /// <summary>The bucket for a formatting, rename, or nitpick change.</summary>
    public const string Formatting = "formatting";

    /// <summary>The bucket for a route or API surface change.</summary>
    public const string RouteApi = "route-api";

    /// <summary>The bucket for a test-only change.</summary>
    public const string TestOnly = "test-only";

    /// <summary>The bucket for an identifier-rich title (carries a code identifier token).</summary>
    public const string IdentifierRich = "identifier-rich";

    /// <summary>The bucket for a natural-language domain title with no obvious identifier.</summary>
    public const string NaturalLanguage = "nl-domain";

    /// <summary>
    ///     Classifies a task title into a signal bucket.
    /// </summary>
    /// <param name="title">The task title.</param>
    /// <returns>One of the bucket constants on this type.</returns>
    public static string Classify(string? title)
    {
        var t = title?.Trim() ?? string.Empty;
        if (t.Length == 0 || MergeNoise().IsMatch(t))
            return NoSignal;
        if (DependencyNoise().IsMatch(t))
            return DependencyBump;
        if (CiNoise().IsMatch(t))
            return ConfigCi;
        if (FormattingNoise().IsMatch(t))
            return Formatting;
        if (RouteWords().IsMatch(t))
            return RouteApi;
        if (TestWords().IsMatch(t))
            return TestOnly;
        if (Identifier().IsMatch(t))
            return IdentifierRich;
        return NaturalLanguage;
    }

    /// <summary>
    ///     Reports whether a bucket is one query retrieval cannot solve from the title alone, so that
    ///     Fuse should detect low signal rather than answer overconfidently.
    /// </summary>
    /// <param name="bucket">A bucket constant from <see cref="Classify" />.</param>
    /// <returns><see langword="true" /> for the no-signal bucket.</returns>
    public static bool IsLowSignal(string bucket) => bucket == NoSignal;

    // "Merge branch ...", "Merge pull request ...", "Apply suggestions from code review" carry no scope.
    [GeneratedRegex(@"^(merge\b|apply suggestions from code review)", RegexOptions.IgnoreCase)]
    private static partial Regex MergeNoise();

    [GeneratedRegex(@"^(bump|upgrade)\b|\bdependabot\b|\bfrom\s+v?\d[\w.\-]*\s+to\s+v?\d[\w.\-]*", RegexOptions.IgnoreCase)]
    private static partial Regex DependencyNoise();

    [GeneratedRegex(@"^(ci|build|chore)(\(|:|\b)", RegexOptions.IgnoreCase)]
    private static partial Regex CiNoise();

    [GeneratedRegex(@"\b(format|formatting|whitespace|typo|nitpick|cleanup|style|rename)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FormattingNoise();

    [GeneratedRegex(@"\b(route|routes|routing|endpoint|controller|http|api|minimal api)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RouteWords();

    [GeneratedRegex(@"\b(test|tests|spec|specs|unit test)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TestWords();

    // A token with an internal capital (PascalCase/camelCase), a method-call shape, or a .cs reference.
    [GeneratedRegex(@"[a-z][A-Z]|[A-Z][a-z]+[A-Z]|\w+\(\)|\.cs\b|\b[A-Z]\w*\.[A-Z]\w*")]
    private static partial Regex Identifier();
}
