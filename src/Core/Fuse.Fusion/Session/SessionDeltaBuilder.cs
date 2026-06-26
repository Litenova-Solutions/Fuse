using System.Text;

namespace Fuse.Fusion.Session;

/// <summary>
///     Builds the note that records which files were omitted from a session-delta fusion because the agent
///     already holds them.
/// </summary>
public static class SessionDeltaBuilder
{
    /// <summary>
    ///     Renders the session-delta note recording which files were omitted as unchanged and which were sent as
    ///     a diff because they changed since an earlier turn.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="alreadySent">The normalized paths of files omitted because they were sent earlier unchanged.</param>
    /// <param name="diffed">The normalized paths of files sent as a unified diff because they changed since an earlier turn.</param>
    /// <returns>
    ///     A comment naming the omitted and diffed files terminated by a newline, or <see cref="string.Empty" />
    ///     when neither occurred (for example, the first fusion of a session).
    /// </returns>
    public static string BuildNote(string sessionId, IReadOnlyList<string> alreadySent, IReadOnlyList<string> diffed)
    {
        if (alreadySent.Count == 0 && diffed.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.Append("<!-- fuse:session-delta session=").Append(sessionId).Append('\n');

        if (alreadySent.Count > 0)
        {
            sb.Append("     omitted ").Append(alreadySent.Count).Append(" file(s) already sent unchanged this session:\n");
            foreach (var path in alreadySent.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                sb.Append("       ").Append(path).Append('\n');
        }

        if (diffed.Count > 0)
        {
            sb.Append("     sent ").Append(diffed.Count).Append(" changed file(s) as a unified diff, not the whole file:\n");
            foreach (var path in diffed.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                sb.Append("       ").Append(path).Append('\n');
        }

        sb.Append("     Re-request a file by path (or reset the session) if you need its full current content. -->\n");
        return sb.ToString();
    }
}
