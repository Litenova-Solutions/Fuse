using Fuse.Protocol;

namespace Fuse.Hooks;

/// <summary>
///     The text a blocked stop carries: a delta line first when the session met a blocked stop before, the errors as
///     the check printed them with the changed-declarations part of the summary held back, one line per project whose
///     declarations changed, and a last line that says what to do. The full list stays in
///     <c>last-check-results.txt</c>, which the check writes on every run. When every error sits in a file the session
///     never wrote and none is caused by the session's own edits, the last line names how to credit those errors to
///     the worker that wrote them instead of saying whose edits cause them. An error in a file the session never
///     wrote that its own declaration change caused, as in a caller of a renamed member, keeps the fix line: the
///     session is the one that can fix it.
/// </summary>
internal static class StopMessage
{
    /// <summary>The line that names how to credit errors in files this session did not write to the worker that did.</summary>
    internal const string GuidanceLine =
        "These errors are in files this session did not write. Register the worker that wrote them with `fuse session begin <name> --paths <glob>` for its paths, or set FUSE_SESSION=<name> for its commands, so its errors are told to it instead.";

    /// <summary>True when <paramref name="errors"/> is not empty and every one sits in a file outside <paramref name="editedFiles"/>.</summary>
    internal static bool OnlyInFilesNeverWritten(IReadOnlyList<ReportedError> errors, IReadOnlySet<string> editedFiles) =>
        errors.Count > 0 && errors.All(e => !editedFiles.Contains(e.Error.Path));

    /// <summary>
    ///     True when one of <paramref name="errors"/> is caused by <paramref name="session"/>'s own edits: the session
    ///     that wrote the file holding the cause's declaration, or one of its stopped subagents, whose edits count as
    ///     its own. Such an error may sit in a file the session never wrote.
    /// </summary>
    internal static bool CausedBySession(IReadOnlyList<ReportedError> errors, string? session) =>
        session is not null
        && errors.Any(e => e.CausedBy is { } causer
                           && (causer == session || causer.StartsWith(session + "/", StringComparison.Ordinal)));

    /// <summary>The first line of a blocked stop that follows an earlier one: how much of the answer that earlier stop did not hold, and how much of that sits in files the session wrote.</summary>
    internal static string DeltaLine(StopMemory.Snapshot? previous, IReadOnlyList<ReportedError> current, IReadOnlySet<string> editedFiles)
    {
        var (unseen, inEdited) = StopMemory.Delta(previous, current, editedFiles);
        return $"{unseen} new since your last stop, {inEdited} in files you edited";
    }

    /// <summary>The blocked stop text for one repository, from the check text the operation rendered.</summary>
    /// <param name="checkText">The check answer for this repository, summary last.</param>
    /// <param name="report">The report that answer rendered, which names the projects whose declarations changed.</param>
    /// <param name="previous">The blocked answer the session met before in this repository, or null.</param>
    /// <param name="editedFiles">Repository-relative paths the session wrote in this repository.</param>
    /// <param name="session">The session that is stopping, or null when its harness names none.</param>
    internal static string Build(string checkText, CheckReport report, StopMemory.Snapshot? previous, IReadOnlySet<string> editedFiles, string? session)
    {
        var lines = new List<string>();
        if (previous is not null)
            lines.Add(DeltaLine(previous, report.Errors, editedFiles));
        lines.Add(WithoutDeclarationsPart(checkText, report));
        foreach (var project in report.DeclarationsChangedIn)
            lines.Add($"{project} declarations changed");
        if (OnlyInFilesNeverWritten(report.Errors, editedFiles) && !CausedBySession(report.Errors, session))
        {
            if (session is null)
                lines.Add(HookCommand.StopReasonWithoutSession);
            lines.Add(GuidanceLine);
        }
        else
        {
            lines.Add(session is null ? HookCommand.StopReasonWithoutSession : HookCommand.StopReason);
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    ///     <paramref name="checkText"/> with the changed-declarations part taken out of its summary line, because
    ///     <see cref="Build"/> prints that part as one line per project. A summary without that part is returned as it came.
    /// </summary>
    private static string WithoutDeclarationsPart(string checkText, CheckReport report)
    {
        if (report.DeclarationsChangedIn.Length == 0)
            return checkText;
        var part = $"{string.Join(", ", report.DeclarationsChangedIn)} declarations changed, {report.DependentProjectsChecked} dependent project(s) checked";
        var summaryAt = checkText.LastIndexOf(part, StringComparison.Ordinal);
        if (summaryAt < 0)
            return checkText;
        var text = checkText.Remove(summaryAt, part.Length);
        // The part sat inside a list the summary joins with "; ", so one separator goes with it.
        if (summaryAt + 1 < text.Length && text[summaryAt] == ';' && text[summaryAt + 1] == ' ')
            text = text.Remove(summaryAt, 2);
        else if (summaryAt >= 2 && text[summaryAt - 2] == ';' && text[summaryAt - 1] == ' ')
            text = text.Remove(summaryAt - 2, 2);
        // A summary left holding only its count keeps no empty list behind it.
        return text.Replace(" ;; ", "; ", StringComparison.Ordinal).Replace("(;", "(", StringComparison.Ordinal);
    }
}
