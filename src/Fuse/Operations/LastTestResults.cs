using System.Globalization;
using System.Text.Json.Nodes;
using Fuse.Dotnet;
using Fuse.Paths;

namespace Fuse.Operations;

/// <summary>
///     The structured record of the last test run, as <c>last-test-results.json</c> in the state directory, beside the
///     text file. It holds each run project with its absolute project path and per-test verdicts and durations, so
///     <c>fuse test --failed</c> can rerun the failures without parsing text, and an affected run can run them first.
/// </summary>
internal static class LastTestResults
{
    /// <summary>The most tests the record holds; failures come first, so a larger run keeps its failures.</summary>
    internal const int MaxTests = 20000;

    private const int Version = 1;

    private const string FileName = "last-test-results.json";

    /// <summary>
    ///     The last run's projects as (absolute project path, display name, tests). Null when no run is recorded, or the
    ///     file is unreadable, malformed, or of another version. It never throws.
    /// </summary>
    internal static IReadOnlyList<(string Project, string Name, IReadOnlyList<TestRecord> Tests)>? Read(RepoRoot root)
    {
        try
        {
            var path = Path.Combine(root.StateDirectory, FileName);
            if (!File.Exists(path))
                return null;
            var parsed = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (parsed is null || (int?)parsed["version"] != Version || parsed["projects"] is not JsonArray projects)
                return null;
            var result = new List<(string Project, string Name, IReadOnlyList<TestRecord> Tests)>(projects.Count);
            foreach (var project in projects)
            {
                if (project is not JsonObject entry
                    || entry["project"]?.GetValue<string>() is not string projectPath
                    || entry["name"]?.GetValue<string>() is not string name
                    || entry["tests"] is not JsonArray tests)
                    return null;
                var records = new List<TestRecord>(tests.Count);
                foreach (var test in tests)
                {
                    if (test is not JsonObject stored
                        || stored["name"]?.GetValue<string>() is not string testName
                        || stored["verdict"]?.GetValue<string>() is not string verdictText
                        || Verdict(verdictText) is not { } verdict
                        || stored["durationMs"]?.ToString() is not string millisecondsText
                        || !double.TryParse(millisecondsText, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds))
                        return null;
                    records.Add(new TestRecord(testName, verdict, Milliseconds(milliseconds)));
                }

                result.Add((projectPath, name, records));
            }

            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Replaces the record with <paramref name="runs"/>: per-test verdicts and durations for at most
    ///     <see cref="MaxTests"/> tests, failures first. The file is replaced atomically, through a temporary file
    ///     beside it. It never throws.
    /// </summary>
    internal static void Write(RepoRoot root, IEnumerable<(string Project, string Name, TrxResults Outcome)> runs)
    {
        try
        {
            var ordered = runs.ToList();
            var flattened = new List<(string Project, string Name, TestRecord Record)>();
            foreach (var (project, name, outcome) in ordered)
            {
                var records = outcome.Records.Count > 0 ? outcome.Records : outcome.Failures.Select(f => new TestRecord(f.Name, TestVerdict.Failed, TimeSpan.Zero));
                foreach (var record in records)
                    flattened.Add((project, name, record));
            }

            var kept = flattened.Where(t => t.Record.Verdict == TestVerdict.Failed).Concat(flattened.Where(t => t.Record.Verdict != TestVerdict.Failed)).Take(MaxTests).ToList();
            var projects = new JsonArray();
            foreach (var group in kept.GroupBy(t => (t.Project, t.Name)))
            {
                var tests = new JsonArray();
                foreach (var (_, _, record) in group)
                {
                    tests.Add((JsonNode)new JsonObject
                    {
                        ["name"] = record.Name,
                        ["verdict"] = record.Verdict.ToString(),
                        ["durationMs"] = record.Duration.TotalMilliseconds,
                    });
                }

                projects.Add((JsonNode)new JsonObject
                {
                    ["project"] = group.Key.Project,
                    ["name"] = group.Key.Name,
                    ["tests"] = tests,
                });
            }

            Directory.CreateDirectory(root.StateDirectory);
            var path = Path.Combine(root.StateDirectory, FileName);
            var temp = path + ".fuse-tmp";
            File.WriteAllText(temp, new JsonObject { ["version"] = Version, ["projects"] = projects }.ToJsonString());
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            try
            {
                File.Delete(Path.Combine(root.StateDirectory, FileName + ".fuse-tmp"));
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static TestVerdict? Verdict(string text) => text switch
    {
        nameof(TestVerdict.Passed) => TestVerdict.Passed,
        nameof(TestVerdict.Failed) => TestVerdict.Failed,
        nameof(TestVerdict.Skipped) => TestVerdict.Skipped,
        _ => null,
    };

    /// <summary><paramref name="milliseconds"/> as a span; a value no span holds reads as zero.</summary>
    private static TimeSpan Milliseconds(double milliseconds)
    {
        try
        {
            return double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) ? TimeSpan.Zero : TimeSpan.FromMilliseconds(milliseconds);
        }
        catch (Exception e) when (e is OverflowException or ArgumentException)
        {
            return TimeSpan.Zero;
        }
    }
}
