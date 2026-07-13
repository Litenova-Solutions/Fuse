using System.Text;
using System.Text.Json;

namespace Fuse.Benchmarks;

/// <summary>
///     Classifies a Claude Code CLI <c>stream-json</c> task-resolution transcript into the ordered turns the
///     loop metrics (R4) count: which turns were build or test verifications and whether each passed. This is
///     the deterministic, model-free core of the loop suite, so it is unit-tested against a scripted transcript
///     and carries the R4 claim between the expensive model-driven runs, exactly as <see cref="LoopMetrics" />
///     does for the computation over the turns.
/// </summary>
/// <remarks>
///     A verification turn is a <c>tool_use</c> that invokes the compiler or the test runner: a <c>Bash</c>
///     command containing <c>dotnet build</c> or <c>dotnet test</c>, or the <c>fuse_check</c> speculative
///     typecheck. The three are classified into distinct kinds (<c>Build</c>, <c>Test</c>, <c>Check</c>) so the
///     loop-collapse metric can count agent-visible build round-trips separately from speculative checks (D22a);
///     R4's thesis is that <c>fuse_check</c> stands in for a <c>dotnet build</c> round-trip, and measuring that
///     substitution requires the two never share a column. Pass or fail is read from the paired
///     <c>tool_result</c>: an error-indicating result (a nonzero exit, a compiler error id, or a "cannot verify"
///     abstention) is a failed turn. A read tool is a <c>Read</c> turn, an <c>Edit</c> or <c>Write</c> is an edit
///     turn; everything else is <c>Other</c>.
/// </remarks>
public static class LoopTranscriptClassifier
{
    /// <summary>
    ///     Classifies a transcript into ordered turns.
    /// </summary>
    /// <param name="transcript">The newline-delimited stream-json transcript from the CLI.</param>
    /// <returns>The ordered turns; empty when the transcript has no tool calls.</returns>
    public static IReadOnlyList<TranscriptTurn> Classify(string transcript)
    {
        // A tool_use and its matching tool_result arrive on different lines (assistant then user), keyed by
        // tool_use id. Collect the verification tool calls first, then resolve each verdict from its result.
        var turns = new List<PendingTurn>();
        var resultsById = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in transcript.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl))
                    continue;
                var type = typeEl.GetString();

                if (type == "assistant" && TryContent(root, out var content))
                {
                    foreach (var c in content.EnumerateArray())
                    {
                        if (!(c.TryGetProperty("type", out var ct) && ct.GetString() == "tool_use"))
                            continue;
                        var name = c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var id = c.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                        var command = c.TryGetProperty("input", out var input) && input.TryGetProperty("command", out var cmd)
                            ? cmd.GetString() ?? ""
                            : "";
                        turns.Add(new PendingTurn(id, KindOf(name, command)));
                    }
                }
                else if (type == "user" && TryContent(root, out var userContent))
                {
                    foreach (var c in userContent.EnumerateArray())
                    {
                        if (c.TryGetProperty("type", out var ct) && ct.GetString() == "tool_result"
                            && c.TryGetProperty("tool_use_id", out var idEl) && idEl.GetString() is { } id)
                            resultsById[id] = ExtractResultText(c);
                    }
                }
            }
        }

        var result = new List<TranscriptTurn>(turns.Count);
        foreach (var t in turns)
        {
            var passed = t.Kind is TurnKind.Build or TurnKind.Check or TurnKind.Test
                         && resultsById.TryGetValue(t.Id, out var text)
                         && !IndicatesFailure(text);
            result.Add(new TranscriptTurn(t.Kind, passed, 0));
        }

        return result;
    }

    private static TurnKind KindOf(string toolName, string command)
    {
        // The fuse_check speculative typecheck is a verification turn but NOT an agent-visible build round-trip:
        // it gets its own Check column so the loop-collapse metric can count real dotnet-build turns separately
        // (D22a; the first B1 harness gap was folding fuse_check into the build column).
        if (toolName.Contains("fuse_check", StringComparison.Ordinal))
            return TurnKind.Check;
        if (toolName.Equals("Read", StringComparison.Ordinal) || toolName.Equals("Grep", StringComparison.Ordinal)
            || toolName.Equals("Glob", StringComparison.Ordinal) || toolName.StartsWith("mcp__fuse", StringComparison.Ordinal))
            return TurnKind.Read;
        if (toolName.Equals("Edit", StringComparison.Ordinal) || toolName.Equals("Write", StringComparison.Ordinal)
            || toolName.Equals("MultiEdit", StringComparison.Ordinal))
            return TurnKind.Edit;
        if (toolName.Equals("Bash", StringComparison.Ordinal))
        {
            if (command.Contains("dotnet test", StringComparison.OrdinalIgnoreCase))
                return TurnKind.Test;
            if (command.Contains("dotnet build", StringComparison.OrdinalIgnoreCase)
                || command.Contains("dotnet run", StringComparison.OrdinalIgnoreCase))
                return TurnKind.Build;
            return TurnKind.Other;
        }

        return TurnKind.Other;
    }

    // A verification turn failed if its result text carries a failure signal: a compiler error id, a build/test
    // failure line, a nonzero exit, or a fuse_check abstention. Absent any signal, the turn is treated as passed.
    private static bool IndicatesFailure(string text)
    {
        return text.Contains("error CS", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Failed!", StringComparison.Ordinal)
               || text.Contains("Failed:", StringComparison.Ordinal) && !text.Contains("Failed:     0", StringComparison.Ordinal)
               || text.Contains("cannot verify", StringComparison.OrdinalIgnoreCase)
               || text.Contains("diagnostics for", StringComparison.OrdinalIgnoreCase)
               || System.Text.RegularExpressions.Regex.IsMatch(text, @"exit(?:\s*code)?[:\s]+[1-9]");
    }

    private static bool TryContent(JsonElement root, out JsonElement content)
    {
        content = default;
        return root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out content)
            ? content.ValueKind == JsonValueKind.Array
            : root.TryGetProperty("content", out content) && content.ValueKind == JsonValueKind.Array;
    }

    private static string ExtractResultText(JsonElement toolResult)
    {
        if (!toolResult.TryGetProperty("content", out var content))
            return string.Empty;
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;
        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in content.EnumerateArray())
                if (item.TryGetProperty("text", out var t))
                    sb.AppendLine(t.GetString());
            return sb.ToString();
        }

        return string.Empty;
    }

    private sealed record PendingTurn(string Id, TurnKind Kind);
}
