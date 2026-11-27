using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Fuse.Client;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
using Fuse.Protocol.Transport;
namespace Fuse.Operations.Judge;

/// <summary>
///     <c>fuse judge --combine</c> and <c>fuse judge --race</c>: reads unified diff patches or git worktree diffs
///     on the client side, sends them to the engine's in-memory speculative evaluation endpoint, and renders the
///     combined verdict or ranked candidate scorecard.
/// </summary>
internal static class JudgeOperation
{
    internal const string Usage =
        "usage: fuse judge (--combine | --race) <patch-or-worktree...> [--json]";

    internal sealed record JudgeArguments(
        JudgeRequest.JudgeMode Mode,
        IReadOnlyList<string> Targets,
        bool Json);

    public static ParseResult<JudgeArguments> Parse(IReadOnlyList<string> args)
    {
        JudgeRequest.JudgeMode? mode = null;
        var json = false;
        var targets = new List<string>();
        var options = true;

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (token == "--" && options)
            {
                options = false;
                continue;
            }

            if (!options)
            {
                targets.Add(token);
                continue;
            }

            if (token is "--help" or "-h")
                continue;
            if (token == "--combine")
            {
                if (mode == JudgeRequest.JudgeMode.Race)
                    return new ParseResult<JudgeArguments>.Refused("fuse: --combine and --race cannot be combined; choose one");
                mode = JudgeRequest.JudgeMode.Combine;
                continue;
            }

            if (token == "--race")
            {
                if (mode == JudgeRequest.JudgeMode.Combine)
                    return new ParseResult<JudgeArguments>.Refused("fuse: --combine and --race cannot be combined; choose one");
                mode = JudgeRequest.JudgeMode.Race;
                continue;
            }

            if (token == "--json")
            {
                json = true;
                continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
                return new ParseResult<JudgeArguments>.Refused($"fuse: '{token}' is not a fuse judge option\n{Usage}");
            targets.Add(token);
        }

        if (mode is null)
            return new ParseResult<JudgeArguments>.Refused($"fuse: specify --combine or --race\n{Usage}");
        if (targets.Count == 0)
            return new ParseResult<JudgeArguments>.Refused($"fuse: name at least one patch file or worktree directory\n{Usage}");

        return new ParseResult<JudgeArguments>.Parsed(new JudgeArguments(mode.Value, targets, json));
    }

    public static async Task<OperationResult> RunAsync(
        RepoRoot root,
        string workingDirectory,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var parsed = Parse(args);
        JudgeArguments arguments;
        switch (parsed)
        {
            case ParseResult<JudgeArguments>.Parsed ok:
                arguments = ok.Value;
                break;
            case ParseResult<JudgeArguments>.Refused refused:
                return new OperationResult(Outcome.Unanswered, refused.Message);
            default:
                throw new UnreachableException($"a judge parse is parsed or refused, not {parsed.GetType().Name}");
        }

        if (arguments.Targets.Count > JudgeLimits.MaxCandidates)
            return new OperationResult(Outcome.Unanswered, $"fuse: the {arguments.Targets.Count} candidates named are more than the {JudgeLimits.MaxCandidates} a judge call scores; name fewer");

        var candidates = new List<JudgeRequest.Candidate>(arguments.Targets.Count);
        long totalBytes = 0;
        foreach (var target in arguments.Targets)
        {
            var loaded = await LoadCandidateAsync(target, workingDirectory, cancellationToken).ConfigureAwait(false);
            JudgeRequest.Candidate candidate;
            switch (loaded)
            {
                case ParseResult<JudgeRequest.Candidate>.Parsed scored:
                    candidate = scored.Value;
                    break;
                case ParseResult<JudgeRequest.Candidate>.Refused loadRefused:
                    return new OperationResult(Outcome.Unanswered, loadRefused.Message);
                default:
                    throw new UnreachableException($"a judge load is parsed or refused, not {loaded.GetType().Name}");
            }

            var bytes = Encoding.UTF8.GetByteCount(candidate.Diff);
            if (bytes > JudgeLimits.MaxDiffBytes)
                return new OperationResult(Outcome.Unanswered, $"fuse: the diff of '{candidate.Name}' holds {bytes} bytes, more than the 1 MB a candidate holds; shorten the diff");
            totalBytes += bytes;
            if (totalBytes > JudgeLimits.MaxTotalDiffBytes)
                return new OperationResult(Outcome.Unanswered, $"fuse: the candidates hold {totalBytes} bytes of diff, more than the 8 MB a judge call scores; name fewer or shorten the diffs");
            candidates.Add(candidate);
        }

        var request = new EngineRequest.Judge(new JudgeRequest(arguments.Mode, [.. candidates]));
        var response = await EngineClient.SendAsync(root, request, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        if (response is not EngineResponse.JudgeAnswered answered)
            return OperationResult.Unanswered(response, "judge report");

        return Render(answered.Report, arguments.Json);
    }

    internal static async Task<ParseResult<JudgeRequest.Candidate>> LoadCandidateAsync(
        string spec,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var (candidate, refusal) = await CandidateDiffLoader.LoadCandidateAsync(spec, workingDirectory, cancellationToken).ConfigureAwait(false);
        if (candidate is not null)
            return new ParseResult<JudgeRequest.Candidate>.Parsed(candidate);
        return new ParseResult<JudgeRequest.Candidate>.Refused(refusal ?? $"fuse: patch or worktree '{spec}' does not exist");
    }

    internal static OperationResult Render(JudgeReport report, bool json)
    {
        var outcome = report.Clean ? Outcome.Clean : Outcome.ProblemsFound;
        if (json)
            return new OperationResult(outcome, JsonSerializer.Serialize(report, ProtocolJson.Default.JudgeReport));

        var sb = new StringBuilder();
        sb.Append($"fuse judge ({report.Mode}): {report.Summary}");
        foreach (var conflict in report.Conflicts)
            sb.Append("\n  conflict: ").Append(conflict);

        foreach (var verdict in report.Verdicts.OrderBy(v => v.Rank))
        {
            var compilePart = verdict.Compiles ? "compiles" : $"{verdict.ErrorCount} error(s)";
            sb.Append($"\n  #{verdict.Rank} {verdict.Name}: {compilePart} ({verdict.ElapsedMs} ms)");
            if (verdict.ConflictReason is { Length: > 0 } reason)
                sb.Append($"\n    conflict: {reason}");
            foreach (var error in verdict.Errors.Take(10))
                sb.Append("\n    ").Append(error.Error);
        }

        if (report.Winner is { Length: > 0 } winner)
            sb.Append($"\nwinner: {winner}");

        return new OperationResult(outcome, sb.ToString());
    }
}
