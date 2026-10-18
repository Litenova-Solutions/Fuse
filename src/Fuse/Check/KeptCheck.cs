using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Fuse.Check.Model;

namespace Fuse.Check;

/// <summary>
///     The answers of recent checks by what the tree held when they ran, so a check with nothing changed since is
///     answered without binding anything again. It lives in memory only; an engine that restarts checks again.
/// </summary>
internal sealed class KeptCheck
{
    /// <summary>The most answers kept; past it the answer unused the longest goes.</summary>
    private const int MaxEntries = 8;

    private readonly Dictionary<string, CheckResult> _answers = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    /// <summary>The kept answer for <paramref name="key"/>, or false when nothing is kept for it.</summary>
    public bool TryGet(string key, out CheckResult result)
    {
        var found = _answers.TryGetValue(key, out var value);
        result = value!;
        return found;
    }

    /// <summary>
    ///     Keeps <paramref name="result"/> for <paramref name="key"/>, forgetting the answer unused the longest past the
    ///     cap. A result whose analyzers did not finish is kept for nothing: it holds only what finished in time, so a
    ///     repeat runs the check again.
    /// </summary>
    public void Keep(string key, CheckResult result)
    {
        if (result.AnalyzersUnfinished > 0)
            return;
        if (_answers.ContainsKey(key))
        {
            _answers[key] = result;
            _order.Remove(key);
            _order.Add(key);
            return;
        }

        if (_order.Count >= MaxEntries)
        {
            _answers.Remove(_order[0]);
            _order.RemoveAt(0);
        }

        _answers[key] = result;
        _order.Add(key);
    }

    /// <summary>
    ///     The key for a check of <paramref name="scopeKey"/> answered to <paramref name="session"/>, or null when a
    ///     changed file cannot be read: the check runs then instead of answering from a key that leaves a file out.
    /// </summary>
    /// <param name="scopeKey">The scope checked: all changes, or the named files in sorted order.</param>
    /// <param name="session">The session the answer is for, or null for every error.</param>
    /// <param name="compilationWide">Whether the check runs the analyzers that read the whole compilation.</param>
    /// <param name="headIdentity">The commit the baseline is taken from.</param>
    /// <param name="configurationGeneration">The workspace generation, which moves when projects are evaluated again.</param>
    /// <param name="sessionRecordsHash">A hash of which sessions wrote each changed file.</param>
    /// <param name="changedFiles">Every file that differs from HEAD, with its absolute path to read.</param>
    public static string? KeyFor(
        string scopeKey,
        string? session,
        bool compilationWide,
        string headIdentity,
        int configurationGeneration,
        string sessionRecordsHash,
        IEnumerable<(string Relative, string Absolute)> changedFiles)
    {
        List<string> lines;
        try
        {
            lines = changedFiles
                .OrderBy(f => f.Relative, StringComparer.Ordinal)
                .Select(f => $"{f.Relative}={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f.Absolute)))}")
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var parts = new List<string>(lines.Count + 6)
        {
            scopeKey,
            session ?? "",
            compilationWide ? "1" : "0",
            headIdentity,
            configurationGeneration.ToString(CultureInfo.InvariantCulture),
            sessionRecordsHash,
        };
        parts.AddRange(lines);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts))));
    }
}
