using System.Text.Json;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Check;
using Fuse.Protocol.Transport;
namespace Fuse.Operations.Session;

/// <summary>Renders watch events as text or NDJSON and stored session answers as text.</summary>
internal static class WatchRenderer
{
    internal static async Task WriteWatchEventAsync(WatchEvent ev, bool json, TextWriter output, CancellationToken cancellationToken)
    {
        var line = ev.ToLine();
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(line, ProtocolJson.Default.WatchEventLine)).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (line.EventType == "guard_violation")
        {
            await output.WriteLineAsync($"fuse: guard {line.GuardName} violation on {line.ViolatedPattern}: {line.Declaration ?? line.Message}").ConfigureAwait(false);
        }
        else
        {
            foreach (var error in line.Errors)
                await output.WriteLineAsync(error.Error.ToString()).ConfigureAwait(false);
            var who = line.Session is { Length: > 0 } s ? $"session {s}: " : "";
            var summary = line.Message ?? (line.ErrorCount == 0
                ? $"no errors introduced; generation {line.Generation}; tree {line.Tree}"
                : $"{line.ErrorCount} error(s) introduced; generation {line.Generation}; tree {line.Tree}");
            await output.WriteLineAsync($"fuse: {who}{summary}").ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<bool> TryEmitSessionContentAsync(string content, string fallbackSession, bool json, TextWriter output, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var stored = document.RootElement;
            if (stored.ValueKind != JsonValueKind.Object || !stored.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
                return false;
            var session = stored.TryGetProperty("session", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? fallbackSession : fallbackSession;
            var generation = stored.TryGetProperty("generation", out var number) && number.ValueKind == JsonValueKind.Number ? number.GetInt64() : 0;
            var tree = stored.TryGetProperty("tree", out var stamp) && stamp.ValueKind == JsonValueKind.String ? stamp.GetString() ?? "" : "";
            var count = stored.TryGetProperty("errorCount", out var total) && total.ValueKind == JsonValueKind.Number ? total.GetInt32() : errors.GetArrayLength();

            if (json)
            {
                var reported = new List<ReportedError>();
                foreach (var error in errors.EnumerateArray())
                {
                    if (ToReportedError(error) is { } re)
                        reported.Add(re);
                }

                var line = new WatchEvent.SessionUpdated { Session = session, Generation = generation, Tree = tree, ErrorCount = count, Errors = [.. reported] }.ToLine();
                await output.WriteLineAsync(JsonSerializer.Serialize(line, ProtocolJson.Default.WatchEventLine)).ConfigureAwait(false);
                return true;
            }

            foreach (var error in errors.EnumerateArray())
                output.WriteLine(TextOf(error));
            output.WriteLine(count == 0
                ? $"fuse: session {fallbackSession}: no errors introduced; generation {generation}; tree {tree}"
                : $"fuse: session {fallbackSession}: {count} error(s) introduced; generation {generation}; tree {tree}");
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static ReportedError? ToReportedError(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object)
            return null;
        var path = error.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        var line = error.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
        var col = error.TryGetProperty("column", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
        var id = error.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : "";
        var msg = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
        var fromAnalyzer = error.TryGetProperty("fromAnalyzer", out var fa) && fa.ValueKind == JsonValueKind.True;
        return ReportedError.Create(path, line, col, id, msg, fromAnalyzer);
    }

    /// <summary>One stored error as its canonical line, from its fields or from the line the writer stored with it.</summary>
    private static string TextOf(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.Object)
        {
            if (error.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String && text.GetString() is { Length: > 0 } line)
                return line;
            if (error.TryGetProperty("path", out var path) && error.TryGetProperty("id", out var id) && error.TryGetProperty("message", out var message))
            {
                var lineNumber = error.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
                var column = error.TryGetProperty("column", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
                return $"{path.GetString()}({lineNumber},{column}): error {id.GetString()}: {message.GetString()}";
            }
        }

        return error.ToString();
    }
}
