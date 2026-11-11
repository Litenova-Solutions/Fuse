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

    /// <summary>The most failures one stored run holds, in run order; the rest are only counted.</summary>
    internal const int MaxStoredFailures = 20000;

    private const int Version = 1;

    private const string FileName = "last-test-results.json";

    private const int RunVersion = 1;

    private const string RunFileName = "last-test-run.json";

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
                    || RecordList(entry["tests"]) is not { } records)
                    return null;
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
                    tests.Add((JsonNode)RecordNode(record));

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

    /// <summary>
    ///     Replaces the run record with the finished test run: the plan it ran, the tree it ran on, and what it found,
    ///     so an identical run prints this instead of running again. The file is replaced atomically, through a
    ///     temporary file beside it. It never throws.
    /// </summary>
    /// <param name="treeHash">The tree stamp over the changed sources.</param>
    /// <param name="fullHash">The stamp over every changed file.</param>
    /// <param name="assetsHash">The stamp over the restore outputs.</param>
    /// <param name="planKey">What the run's plan and scope were.</param>
    /// <param name="finishedUtc">When the run finished.</param>
    /// <param name="seconds">How long the run took.</param>
    /// <param name="summary">What ran and why, as the answer said it.</param>
    /// <param name="aggregate">The combined results, with the failures in run order.</param>
    /// <param name="runs">One entry per project, with its results.</param>
    /// <param name="problems">The builds and runs that ended without results, as answered.</param>
    internal static void WriteRun(RepoRoot root, string treeHash, string fullHash, string assetsHash, string planKey, DateTime finishedUtc, double seconds, string summary, TrxResults aggregate, IReadOnlyList<(string Project, string Name, TrxResults Outcome)> runs, IReadOnlyList<OperationResult> problems)
    {
        try
        {
            var flattened = new List<(int Run, TestRecord Record)>();
            for (var i = 0; i < runs.Count; i++)
            {
                foreach (var testRecord in runs[i].Outcome.Records)
                    flattened.Add((i, testRecord));
            }

            var kept = flattened.Where(t => t.Record.Verdict == TestVerdict.Failed).Concat(flattened.Where(t => t.Record.Verdict != TestVerdict.Failed)).Take(MaxTests).ToList();
            var byRun = kept.GroupBy(t => t.Run).ToDictionary(group => group.Key, group => group.Select(t => t.Record).ToList());
            var stored = new JsonArray();
            for (var i = 0; i < runs.Count; i++)
            {
                var (project, name, outcome) = runs[i];
                var records = new JsonArray();
                foreach (var testRecord in byRun.GetValueOrDefault(i) ?? [])
                    records.Add((JsonNode)RecordNode(testRecord));
                var failures = new JsonArray();
                foreach (var failure in outcome.Failures.Take(MaxStoredFailures))
                    failures.Add((JsonNode)FailureNode(failure));
                stored.Add((JsonNode)new JsonObject
                {
                    ["project"] = project,
                    ["name"] = name,
                    ["passed"] = outcome.Passed,
                    ["failed"] = outcome.Failed,
                    ["skipped"] = outcome.Skipped,
                    ["failures"] = failures,
                    ["records"] = records,
                });
            }

            var aggregateFailures = new JsonArray();
            foreach (var failure in aggregate.Failures.Take(MaxStoredFailures))
                aggregateFailures.Add((JsonNode)FailureNode(failure));
            var storedProblems = new JsonArray();
            foreach (var problem in problems)
            {
                storedProblems.Add((JsonNode)new JsonObject
                {
                    ["outcome"] = problem.Outcome.ToString(),
                    ["text"] = problem.Text,
                });
            }

            var record = new JsonObject
            {
                ["version"] = RunVersion,
                ["treeHash"] = treeHash,
                ["fullHash"] = fullHash,
                ["assetsHash"] = assetsHash,
                ["planKey"] = planKey,
                ["finishedUtc"] = finishedUtc.ToString("o", CultureInfo.InvariantCulture),
                ["seconds"] = seconds,
                ["summary"] = summary,
                ["aggregate"] = new JsonObject
                {
                    ["passed"] = aggregate.Passed,
                    ["failed"] = aggregate.Failed,
                    ["skipped"] = aggregate.Skipped,
                    ["failures"] = aggregateFailures,
                },
                ["runs"] = stored,
                ["problems"] = storedProblems,
            };

            Directory.CreateDirectory(root.StateDirectory);
            var path = Path.Combine(root.StateDirectory, RunFileName);
            var temp = path + ".fuse-tmp";
            File.WriteAllText(temp, record.ToJsonString());
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            try
            {
                File.Delete(Path.Combine(root.StateDirectory, RunFileName + ".fuse-tmp"));
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    ///     The last finished test run's record: its plan key and stamps, when it finished, its combined results, one
    ///     entry per project, and the builds and runs that ended without results. Null when no run is recorded, or the
    ///     file is unreadable, malformed, or of another version. It never throws.
    /// </summary>
    internal static (string TreeHash, string FullHash, string AssetsHash, string PlanKey, DateTime FinishedUtc, double Seconds, string Summary, TrxResults Aggregate, List<(string Project, string Name, TrxResults Outcome)> Runs, List<OperationResult> Problems)? ReadRun(RepoRoot root)
    {
        try
        {
            var path = Path.Combine(root.StateDirectory, RunFileName);
            if (!File.Exists(path))
                return null;
            var parsed = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (parsed is null
                || (int?)parsed["version"] != RunVersion
                || parsed["treeHash"]?.GetValue<string>() is not string treeHash
                || parsed["fullHash"]?.GetValue<string>() is not string fullHash
                || parsed["assetsHash"]?.GetValue<string>() is not string assetsHash
                || parsed["planKey"]?.GetValue<string>() is not string planKey
                || FinishedAt(parsed["finishedUtc"]) is not { } finishedUtc
                || parsed["seconds"]?.ToString() is not string secondsText
                || !double.TryParse(secondsText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                || parsed["summary"]?.GetValue<string>() is not string summary
                || parsed["aggregate"] is not JsonObject aggregateNode
                || aggregateNode["passed"]?.GetValue<int>() is not int passed
                || aggregateNode["failed"]?.GetValue<int>() is not int failed
                || aggregateNode["skipped"]?.GetValue<int>() is not int skipped
                || FailureList(aggregateNode["failures"]) is not { } aggregateFailures
                || parsed["runs"] is not JsonArray runs
                || ProblemList(parsed["problems"]) is not { } problems)
                return null;
            var stored = new List<(string Project, string Name, TrxResults Outcome)>(runs.Count);
            foreach (var run in runs)
            {
                if (run is not JsonObject entry
                    || entry["project"]?.GetValue<string>() is not string project
                    || entry["name"]?.GetValue<string>() is not string name
                    || entry["passed"]?.GetValue<int>() is not int runPassed
                    || entry["failed"]?.GetValue<int>() is not int runFailed
                    || entry["skipped"]?.GetValue<int>() is not int runSkipped
                    || FailureList(entry["failures"]) is not { } failures
                    || RecordList(entry["records"]) is not { } records)
                    return null;
                stored.Add((project, name, new TrxResults(runPassed, runFailed, runSkipped, failures) { Records = records }));
            }

            return (treeHash, fullHash, assetsHash, planKey, finishedUtc, seconds, summary, new TrxResults(passed, failed, skipped, aggregateFailures), stored, problems);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary><paramref name="record"/> as JSON.</summary>
    private static JsonObject RecordNode(TestRecord record) => new()
    {
        ["name"] = record.Name,
        ["verdict"] = record.Verdict.ToString(),
        ["durationMs"] = record.Duration.TotalMilliseconds,
    };

    /// <summary><paramref name="node"/> as per-test records, or null when it is no array of them.</summary>
    private static List<TestRecord>? RecordList(JsonNode? node)
    {
        if (node is not JsonArray tests)
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

        return records;
    }

    /// <summary><paramref name="failure"/> as JSON: its name, its message, and its frames.</summary>
    private static JsonObject FailureNode(TestFailure failure) => new()
    {
        ["name"] = failure.Name,
        ["message"] = failure.Message,
        ["frames"] = new JsonArray([.. failure.Frames.Select(frame => (JsonNode)JsonValue.Create(frame))]),
    };

    /// <summary><paramref name="node"/> as test failures, or null when it is no array of them.</summary>
    private static List<TestFailure>? FailureList(JsonNode? node)
    {
        if (node is not JsonArray array)
            return null;
        var result = new List<TestFailure>(array.Count);
        foreach (var entry in array)
        {
            if (entry is not JsonObject stored
                || stored["name"]?.GetValue<string>() is not string name
                || stored["message"]?.GetValue<string>() is not string message
                || FrameList(stored["frames"]) is not { } frames)
                return null;
            result.Add(new TestFailure(name, message, frames));
        }

        return result;
    }

    /// <summary><paramref name="node"/> as stack frames, or null when it is no array of them.</summary>
    private static List<string>? FrameList(JsonNode? node)
    {
        if (node is not JsonArray array)
            return null;
        var result = new List<string>(array.Count);
        foreach (var entry in array)
        {
            if (entry?.GetValue<string>() is not string frame)
                return null;
            result.Add(frame);
        }

        return result;
    }

    /// <summary><paramref name="node"/> as answered problems, or null when it is no array of them.</summary>
    private static List<OperationResult>? ProblemList(JsonNode? node)
    {
        if (node is not JsonArray array)
            return null;
        var result = new List<OperationResult>(array.Count);
        foreach (var entry in array)
        {
            if (entry is not JsonObject stored
                || stored["outcome"]?.GetValue<string>() is not string outcomeText
                || OutcomeOf(outcomeText) is not { } outcome
                || stored["text"]?.GetValue<string>() is not string text)
                return null;
            result.Add(new OperationResult(outcome, text));
        }

        return result;
    }

    private static Outcome? OutcomeOf(string text) => text switch
    {
        nameof(Outcome.Clean) => Outcome.Clean,
        nameof(Outcome.ProblemsFound) => Outcome.ProblemsFound,
        nameof(Outcome.Unanswered) => Outcome.Unanswered,
        _ => null,
    };

    /// <summary><paramref name="node"/> as the moment the run finished, or null when it names none.</summary>
    private static DateTime? FinishedAt(JsonNode? node) =>
        node?.GetValue<string>() is string text && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var finished) ? finished : null;

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
