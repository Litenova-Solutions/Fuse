using System.Globalization;
using System.Text;

namespace Fuse.Telemetry;

/// <summary>
///     One line the engine writes per request, naming the request and how long each phase of it took:
///     <c>phases id=&lt;id&gt; kind=&lt;kind&gt; [gateHolder=&lt;holder&gt;] &lt;phase&gt;=&lt;ms&gt; ...</c>. The product writes it and
///     the evals read it, so both sides agree on the format. Times are milliseconds to one decimal; the phases are in the
///     order they ran. <c>gateHolder</c> names what held the request lock longest while the request waited for it, and is
///     left out when nothing held it during the wait.
/// </summary>
internal static class PhaseLine
{
    private const string Prefix = "phases ";
    private const string GateHolder = "gateHolder";

    /// <summary>Builds the line for <paramref name="requestId"/> and <paramref name="kind"/> from the timed phases.</summary>
    /// <param name="requestId">The id the client sent.</param>
    /// <param name="kind">The request's case.</param>
    /// <param name="phases">The timed phases, in the order they ran.</param>
    /// <param name="gateHolder">What held the request lock longest while the request waited for it, or null when nothing did.</param>
    public static string Format(string requestId, string kind, IReadOnlyList<(string Phase, double Ms)> phases, string? gateHolder = null)
    {
        var line = new StringBuilder(Prefix).Append("id=").Append(requestId).Append(" kind=").Append(kind);
        if (gateHolder is not null)
            line.Append(' ').Append(GateHolder).Append('=').Append(gateHolder);
        foreach (var (phase, ms) in phases)
            line.Append(' ').Append(phase).Append('=').Append(ms.ToString("0.0", CultureInfo.InvariantCulture));
        return line.ToString();
    }

    /// <summary>
    ///     Reads a line back into its request id, kind and phases. A phase the reader does not know is kept, so a newer
    ///     engine's line still yields the phases this reader does know.
    /// </summary>
    /// <returns>False when the line is not a phase line, or names no request.</returns>
    public static bool TryParse(string? line, out string requestId, out string kind, out Dictionary<string, double> phases) =>
        TryParse(line, out requestId, out kind, out _, out phases);

    /// <summary>Reads a line back into its request id, kind, gate holder and phases.</summary>
    /// <param name="line">The log line.</param>
    /// <param name="requestId">The request id, or empty when the line names none.</param>
    /// <param name="kind">The request's case, or empty when the line names none.</param>
    /// <param name="gateHolder">What held the request lock longest while the request waited for it, or null when the line names nothing.</param>
    /// <param name="phases">The timed phases by name, including any the reader does not know.</param>
    /// <returns>False when the line is not a phase line, or names no request.</returns>
    public static bool TryParse(string? line, out string requestId, out string kind, out string? gateHolder, out Dictionary<string, double> phases)
    {
        requestId = "";
        kind = "";
        gateHolder = null;
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
            else if (name == GateHolder)
                gateHolder = value;
            else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
                phases[name] = ms;
        }

        return requestId.Length > 0;
    }
}
