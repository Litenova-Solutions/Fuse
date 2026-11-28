using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fuse.Identity;
using Fuse.Operations.Session;
using Fuse.Operations.Test;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Check;
namespace Fuse.Operations.Check;

/// <summary>
///     <c>fuse gate</c>: single deterministic worker verification command. Runs <c>fuse check</c> (with optional
///     <c>--brief</c> repair packets), and when clean and <c>--test</c> is set, runs affected tests with fail-fast
///     <c>--max-failures</c>. Exits 0 when green, 1 when errors or test failures remain, 2 when refused.
/// </summary>
internal static partial class GateOperation
{
    internal const string Usage = "usage: fuse gate [--session <name>] [--brief] [--test] [--max-failures <n>] [--json]";

    internal sealed record GateArguments(string? Session, bool Brief, bool RunTests, int MaxFailures, bool Json);

    public static ParseResult<GateArguments> Parse(IReadOnlyList<string> args)
    {
        string? session = null;
        var brief = false;
        var runTests = false;
        var maxFailures = TestOperation.DefaultMaxFailures;
        var json = false;

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (token is "--help" or "-h")
                continue;
            if (token == "--brief")
            {
                brief = true;
                continue;
            }

            if (token == "--test")
            {
                runTests = true;
                continue;
            }

            if (token == "--json")
            {
                json = true;
                continue;
            }

            if (token == "--session" || token.StartsWith("--session=", StringComparison.Ordinal))
            {
                var value = token.StartsWith("--session=", StringComparison.Ordinal)
                    ? token["--session=".Length..]
                    : ++i < args.Count ? args[i] : null;
                if (string.IsNullOrWhiteSpace(value))
                    return new ParseResult<GateArguments>.Refused("fuse: --session takes the session's id");
                session = value;
                continue;
            }

            if (token == "--max-failures" || token.StartsWith("--max-failures=", StringComparison.Ordinal))
            {
                var text = token.StartsWith("--max-failures=", StringComparison.Ordinal)
                    ? token["--max-failures=".Length..]
                    : ++i < args.Count ? args[i] : null;
                if (text is null || !int.TryParse(text, CultureInfo.InvariantCulture, out maxFailures) || maxFailures < 1 || maxFailures > CheckArguments.MaxErrorsLimit)
                    return new ParseResult<GateArguments>.Refused($"fuse: --max-failures takes a count of 1 to {CheckArguments.MaxErrorsLimit}, not '{text ?? ""}'");
                continue;
            }

            return new ParseResult<GateArguments>.Refused($"fuse: '{token}' is not a fuse gate option\n{Usage}");
        }

        return new ParseResult<GateArguments>.Parsed(new GateArguments(session, brief, runTests, maxFailures, json));
    }

    public static async Task<OperationResult> RunAsync(
        RepoRoot root,
        string workingDirectory,
        IReadOnlyList<string> args,
        TextWriter? live,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var parsed = Parse(args);
        if (parsed is ParseResult<GateArguments>.Refused refused)
            return new OperationResult(Outcome.Unanswered, refused.Message);
        var arguments = ((ParseResult<GateArguments>.Parsed)parsed).Value;

        var session = arguments.Session ?? SessionOperation.EnvironmentSession;
        var (checkResult, checkResponse) = await CheckOperation.RunAsync(
            root,
            new CheckInvocation(
                Files: null,
                Session: SessionId.TryParse(session, out var checkSession) ? checkSession : null,
                WaitForLoad: true,
                CompilationWideAnalyzers: true,
                Limit: arguments.Json ? ErrorLimit.All : ErrorLimit.Default,
                Json: false,
                IncludeTreeStamp: true,
                IncludeNonCSharp: true,
                Brief: arguments.Brief,
                InteractiveFast: false,
                OnBusy: BusyNotice.For()),
            TimeSpan.FromMinutes(10),
            cancellationToken).ConfigureAwait(false);

        var checkReport = (checkResponse as EngineResponse.CheckAnswered)?.Report;
        // The check answer already prints its own note; the gate keeps at most one note over both stages.
        var checkText = Advisories.WithoutNotes(checkResult.Text);
        var notes = new List<string>(Advisories.NoteLines(checkResult.Text));
        if (checkResult.Outcome != Outcome.Clean)
            return GateAnswer(arguments, watch, checkResult, checkReport, checkText, null, notes);
        if (!arguments.RunTests)
            return GateAnswer(arguments, watch, checkResult, checkReport, checkText, null, notes);

        var testResult = await TestOperation.RunAsync(new TestInvocation(root, workingDirectory, [], false, session, arguments.Json ? null : live, false, false, false, ErrorLimit.FromCount(arguments.MaxFailures)), cancellationToken).ConfigureAwait(false);
        notes.AddRange(Advisories.NoteLines(testResult.Text));
        return GateAnswer(arguments, watch, testResult, checkReport, checkText, Advisories.WithoutNotes(testResult.Text), notes);
    }

    /// <summary>
    ///     The gate answer over its stages with at most one note: the highest priority note either stage printed,
    ///     in the text and in the JSON answer. Exit codes stay as the stages answered them.
    /// </summary>
    private static OperationResult GateAnswer(
        GateArguments arguments,
        Stopwatch watch,
        OperationResult result,
        CheckReport? checkReport,
        string checkText,
        string? testText,
        List<string> notes)
    {
        var note = Advisories.TopNote(notes);
        string[] advisories = note is null ? [] : [note];
        var failedCheck = testText is null && result.Outcome != Outcome.Clean;
        if (arguments.Json)
        {
            var payload = new GateJsonReport(
                Passed: result.Outcome == Outcome.Clean,
                ExitCode: result.ExitCode,
                Stage: testText is null ? "check" : "test",
                ErrorCount: failedCheck ? (checkReport?.ErrorCount ?? 0) : 0,
                Errors: failedCheck ? (checkReport?.Errors ?? []) : [],
                CheckOutput: checkText,
                TestOutput: testText,
                ElapsedMs: watch.ElapsedMilliseconds)
            {
                Advisories = advisories,
            };
            return new OperationResult(result.Outcome, JsonSerializer.Serialize(payload, GateJson.Default.GateJsonReport));
        }

        var text = testText is null ? checkText : checkText + "\n" + testText;
        if (note is not null)
            text += "\n" + note;
        return result with { Text = text };
    }

    private sealed record GateJsonReport(
        bool Passed,
        int ExitCode,
        string Stage,
        int ErrorCount,
        ReportedError[] Errors,
        string CheckOutput,
        string? TestOutput,
        long ElapsedMs)
    {
        /// <summary>The notes the gate kept, at most one, in priority order. A line without it reads as none.</summary>
        public string[] Advisories { get; init; } = [];
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
    [JsonSerializable(typeof(GateJsonReport))]
    private sealed partial class GateJson : JsonSerializerContext;
}
