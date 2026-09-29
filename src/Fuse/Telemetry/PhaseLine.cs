using System.Globalization;
using System.Text;

namespace Fuse.Telemetry;

/// <summary>
///     One line the engine writes per request, naming the request and how long each phase of it took:
///     <c>phases id=&lt;id&gt; kind=&lt;kind&gt; &lt;phase&gt;=&lt;ms&gt; ...</c>. The product writes it and the evals read it, so
///     both sides agree on the format. Times are milliseconds to one decimal; the phases are in the order they ran.
/// </summary>
internal static class PhaseLine
{
    private const string Prefix = "phases ";

    /// <summary>Builds the line for <paramref name="requestId"/> and <paramref name="kind"/> from the timed phases.</summary>
    public static string Format(string requestId, string kind, IReadOnlyList<(string Phase, double Ms)> phases)
    {
        var line = new StringBuilder(Prefix).Append("id=").Append(requestId).Append(" kind=").Append(kind);
        foreach (var (phase, ms) in phases)
            line.Append(' ').Append(phase).Append('=').Append(ms.ToString("0.0", CultureInfo.InvariantCulture));
        return line.ToString();
    }

    /// <summary>
    ///     Reads a line back into its request id, kind and phases. A phase the reader does not know is kept, so a newer
    ///     engine's line still yields the phases this reader does know.
    /// </summary>
    /// <returns>False when the line is not a phase line, or names no request.</returns>
    public static bool TryParse(string? line, out string requestId, out string kind, out Dictionary<string, double> phases)
    {
        requestId = "";
        kind = "";
        phases = [];
        if (line is null || !line.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        foreach (var token in line[Prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = token.IndexOf('=');
            if (equals <= 0)
                continue;

            var name = token[..equals];
            var value = token[(equals + 1)..];
            if (name == "id")
                requestId = value;
            else if (name == "kind")
                kind = value;
            else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
                phases[name] = ms;
        }

        return requestId.Length > 0;
    }
}
