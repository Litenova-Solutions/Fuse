using System.Text;

namespace Fuse.Fusion.Session;

/// <summary>
///     Builds the note that records which files were omitted from a session-delta fusion because the agent
///     already holds them.
/// </summary>
public static class SessionDeltaBuilder
{
    /// <summary>
    ///     Renders the omitted-files note for a session-delta fusion.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="alreadySent">The normalized paths of files omitted because they were sent earlier unchanged.</param>
    /// <returns>
    ///     A comment naming the omitted files terminated by a newline, or <see cref="string.Empty" /> when
    ///     nothing was omitted (for example, the first fusion of a session).
    /// </returns>
    public static string BuildNote(string sessionId, IReadOnlyList<string> alreadySent)
    {
        if (alreadySent.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.Append("<!-- fuse:session-delta session=").Append(sessionId)
            .Append(" omitted ").Append(alreadySent.Count).Append(" file(s) already sent this session:\n");
        foreach (var path in alreadySent.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            sb.Append("     ").Append(path).Append('\n');
        sb.Append("     Re-request a file by path if you no longer have it. -->\n");
        return sb.ToString();
    }
}
