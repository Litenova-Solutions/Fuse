using Fuse.Identity;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Check;
using Fuse.Protocol.Testing;

namespace Fuse.Operations;

/// <summary>
///     The one line a command prints when a decided limit or a slow repository practice applies. Check, gate and
///     test text answers print at most the highest priority note that applies; status lists every note that applies.
///     Hooks never print one: only a stamped command-line answer carries one. No note changes an exit code.
/// </summary>
internal static class Advisories
{
    /// <summary>How many lines a changed non-generated C# file holds before an answer names it.</summary>
    internal const int LargeFileLines = CheckReport.LargeFileLines;

    /// <summary>How long a session registration may stay open before the status answer names it.</summary>
    internal static readonly TimeSpan StaleSessionAfter = TimeSpan.FromHours(2);

    /// <summary>The ordered notes for a check report, highest priority first.</summary>
    internal static IReadOnlyList<string> CheckNotes(CheckReport report)
    {
        var notes = new List<string>();
        var keptAt = EngineLimits.Default.KeptAnswerAt;
        if (report.TreeFiles >= keptAt)
            notes.Add(KeptAnswerNote(report.TreeFiles, keptAt));
        if (report.LiveSessions.Length > 0)
            notes.Add(ActiveSessionsNote(report.LiveSessions));
        if (report.AnalyzersUnfinished > 0)
            notes.Add(UnfinishedAnalyzersNote(report.AnalyzersUnfinished));
        if (report.LargestTargetFile is not null && report.LargestTargetLines is { } lines && lines >= LargeFileLines)
            notes.Add(LargeFileNote(report.LargestTargetFile, lines));
        return notes;
    }

    /// <summary>The ordered notes for a test plan, highest priority first.</summary>
    internal static IReadOnlyList<string> TestNotes(TestPlan plan)
    {
        foreach (var run in plan.Runs)
        {
            if (!string.IsNullOrWhiteSpace(run.ShadowSkipReason) && FileOf(run.ShadowSkipReason) is { } file)
                return [ShadowSkipNote(run.Name, file)];
        }

        return [];
    }

    /// <summary>The ordered notes for a status answer, highest priority first, as of now.</summary>
    internal static IReadOnlyList<string> StatusNotes(EngineStatus status) =>
        StatusNotes(status, DateTimeOffset.UtcNow);

    /// <summary>The ordered notes for a status answer, highest priority first, as of <paramref name="now"/>.</summary>
    internal static IReadOnlyList<string> StatusNotes(EngineStatus status, DateTimeOffset now) =>
        StatusNotes(status.ChangedFileCount, status.Limits, status.Sessions, now);

    /// <summary>The ordered notes for what a status answer holds, highest priority first.</summary>
    internal static IReadOnlyList<string> StatusNotes(int changedFiles, EngineLimits limits, IReadOnlyList<ReportedSession> sessions, DateTimeOffset now)
    {
        var notes = new List<string>();
        if (changedFiles >= limits.KeptAnswerAt)
            notes.Add(KeptAnswerNote(changedFiles, limits.KeptAnswerAt));
        var stale = sessions
            .Where(session => now - (session.ExpiresAtUtc - SessionId.RegistrationLifetime) >= StaleSessionAfter)
            .Select(session => session.Name)
            .ToList();
        if (stale.Count > 0)
            notes.Add(StaleSessionsNote(stale));
        return notes;
    }

    /// <summary>The highest priority note of <paramref name="notes"/>, or null when it holds none.</summary>
    internal static string? TopNote(IEnumerable<string> notes)
    {
        string? top = null;
        var rank = int.MaxValue;
        foreach (var note in notes)
        {
            var order = RankOf(note);
            if (order < rank)
            {
                rank = order;
                top = note;
            }
        }

        return top;
    }

    /// <summary>How early <paramref name="note"/> prints: 1 first, larger later, unknown last.</summary>
    internal static int RankOf(string note)
    {
        if (note.Contains("kept-answer caching is skipped", StringComparison.Ordinal))
            return 1;
        if (note.Contains("session(s) are active", StringComparison.Ordinal))
            return 2;
        if (note.Contains("have been open for over 2 hours", StringComparison.Ordinal))
            return 3;
        if (note.Contains("shadow run skipped for", StringComparison.Ordinal))
            return 4;
        if (note.Contains("did not finish within the 60s budget", StringComparison.Ordinal))
            return 5;
        if (note.Contains("splitting large files keeps", StringComparison.Ordinal))
            return 6;
        return int.MaxValue;
    }

    /// <summary>The note lines of <paramref name="text"/>, in the order they print.</summary>
    internal static IReadOnlyList<string> NoteLines(string text) =>
        [.. text.Split('\n').Where(IsNoteLine)];

    /// <summary>True when <paramref name="line"/> is a note an answer printed.</summary>
    internal static bool IsNoteLine(string line) =>
        line.StartsWith("fuse: note: ", StringComparison.Ordinal);

    /// <summary><paramref name="text"/> without the note lines an answer printed.</summary>
    internal static string WithoutNotes(string text) =>
        string.Join('\n', text.Split('\n').Where(line => !IsNoteLine(line)));

    private static string KeptAnswerNote(int count, int limit) =>
        $"fuse: note: {count} uncommitted files since HEAD (past {limit}, kept-answer caching is skipped); commit completed work to keep checks fast";

    private static string ActiveSessionsNote(string[] sessions) =>
        $"fuse: note: {sessions.Length} session(s) are active ({string.Join(", ", sessions)}); pass --session <name> or set FUSE_SESSION=judge to isolate results";

    private static string StaleSessionsNote(IReadOnlyList<string> names) =>
        $"fuse: note: session(s) {string.Join(", ", names)} have been open for over 2 hours; run 'fuse session end <name>' if finished";

    private static string ShadowSkipNote(string project, string file) =>
        $"fuse: note: shadow run skipped for {project} ({file} walks parent directories from AppContext.BaseDirectory); use [CallerFilePath] for faster test runs";

    private static string UnfinishedAnalyzersNote(int count) =>
        $"fuse: note: {count} analyzer(s) did not finish within the 60s budget; check their CompilationStart and AdditionalFiles cost";

    private static string LargeFileNote(string relativePath, int lines) =>
        $"fuse: note: {relativePath} is {lines} lines; splitting large files keeps Roslyn binding and edits fast";

    /// <summary>The file the shadow skip reason names, or null when it names none.</summary>
    private static string? FileOf(string reason)
    {
        var index = reason.LastIndexOf(" in ", StringComparison.Ordinal);
        var file = (index >= 0 ? reason[(index + 4)..] : reason).Trim();
        return file.Length == 0 ? null : file;
    }
}
