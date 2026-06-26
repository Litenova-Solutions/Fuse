using System.Text.RegularExpressions;

namespace Fuse.Retrieval;

/// <summary>
///     Decides whether a localization request carries a usable scoping signal. A title that is merge or
///     dependency or CI noise (or an empty query with no structured input) names no code, so its recall is
///     bounded by the input, not the engine. The honest answer there is to abstain and ask for a base, route,
///     or symbol, rather than return overconfident full-text junk.
/// </summary>
/// <remarks>
///     A request with any structured signal (a route, symbol, service, request, config section, git base, or
///     selected paths) is never low signal: the caller gave a strong target. Only a free-text-query-only
///     request is judged, and only the precise noise classes are flagged, so a solvable prose title is not
///     downgraded. The noise patterns mirror the benchmark's no-signal definition so detection is measured
///     against the same ground truth.
/// </remarks>
public static partial class QuerySignalClassifier
{
    /// <summary>
    ///     Classifies a localization request's signal.
    /// </summary>
    /// <param name="request">The localization request.</param>
    /// <returns>
    ///     A verdict: whether the request is low signal, and if so a suggested next input. A high-signal
    ///     verdict carries no suggestion.
    /// </returns>
    public static SignalVerdict Classify(LocalizationRequest request)
    {
        // Any structured signal is a strong target; never low signal.
        if (!string.IsNullOrWhiteSpace(request.Route)
            || !string.IsNullOrWhiteSpace(request.Focus)
            || !string.IsNullOrWhiteSpace(request.Service)
            || !string.IsNullOrWhiteSpace(request.Request)
            || !string.IsNullOrWhiteSpace(request.ConfigSection)
            || !string.IsNullOrWhiteSpace(request.ChangedSince)
            || request.SelectedPaths is { Count: > 0 })
        {
            return SignalVerdict.HighSignal;
        }

        var query = request.Query?.Trim() ?? string.Empty;
        if (query.Length == 0)
            return Low("Provide a route, symbol, service, request, config section, or a git base.");
        if (MergeNoise().IsMatch(query))
            return Low("This looks like merge or review noise with no code reference. Provide a changed-file base (changedSince), a route, or a symbol.");
        if (DependencyNoise().IsMatch(query))
            return Low("This looks like a dependency or version bump. Provide the affected symbol or a git base instead of the title.");
        if (CiNoise().IsMatch(query))
            return Low("This looks like a CI or build change. Provide the affected file path, symbol, or a git base.");

        return SignalVerdict.HighSignal;
    }

    private static SignalVerdict Low(string suggestion) => new(true, suggestion);

    // Mirrors the benchmark SignalBucket no-signal and dependency/CI rules so engine abstention is measured
    // against the same ground truth.
    [GeneratedRegex(@"^(merge\b|apply suggestions from code review)", RegexOptions.IgnoreCase)]
    private static partial Regex MergeNoise();

    [GeneratedRegex(@"^(bump|upgrade)\b|\bdependabot\b|\bfrom\s+v?\d[\w.\-]*\s+to\s+v?\d[\w.\-]*", RegexOptions.IgnoreCase)]
    private static partial Regex DependencyNoise();

    [GeneratedRegex(@"^(ci|build|chore)(\(|:|\b)", RegexOptions.IgnoreCase)]
    private static partial Regex CiNoise();
}

/// <summary>
///     The signal verdict for a localization request.
/// </summary>
/// <param name="IsLowSignal">Whether the request carries no usable scoping signal.</param>
/// <param name="Suggestion">A suggested next input when low signal; otherwise null.</param>
public readonly record struct SignalVerdict(bool IsLowSignal, string? Suggestion)
{
    /// <summary>A verdict that the request carries a usable signal.</summary>
    public static SignalVerdict HighSignal => new(false, null);
}
