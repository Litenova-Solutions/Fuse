using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fuse.Harnesses;

/// <summary>What <c>fuse hook</c> writes back to the harness for one event, and the exit code it ends with.</summary>
/// <param name="StandardOutput">Written as it is, with no newline. Every harness except Claude Code's post-edit report reads a JSON object here.</param>
/// <param name="StandardError">Written as it is, with no newline. Claude Code shows it to the agent when the exit code is 2.</param>
/// <param name="ExitCode">0, except where a harness reads another code as a signal.</param>
internal sealed record HookAnswer(string StandardOutput, string StandardError, int ExitCode)
{
    // Compiler errors are full of quotes and angle brackets; relaxed escaping keeps the JSON readable in harness logs.
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>No output and exit code 0: the harness carries on as if the hook had not run.</summary>
    public static HookAnswer None { get; } = new("", "", 0);

    /// <summary><paramref name="answer"/> on standard output, and exit code 0.</summary>
    public static HookAnswer Json(JsonObject answer) => new(answer.ToJsonString(Relaxed), "", 0);
}
