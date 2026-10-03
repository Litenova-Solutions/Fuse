using Fuse.Check.Model;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;

namespace Fuse.Check;

/// <summary>
///     Decides which of a check's errors a session is told about, when several sessions write one working tree. The check
///     itself is unchanged: it reads the whole tree against HEAD. Attribution only leaves out, for one session, the errors
///     other sessions' edits caused without it.
/// </summary>
/// <remarks>
///     <para>
///         The files that differ from HEAD fall into three groups for session S: files only S wrote, files only other
///         sessions wrote, and the rest, which are files several sessions wrote and files no session is credited with
///         because a shell command, a generator or a tool Fuse never saw changed them. The rest stay in every view, so an
///         error they cause is everyone's.
///     </para>
///     <para>For each error in a file S did not write together with another session:</para>
///     <list type="number">
///         <item>
///             It is bound again in a view of the working tree with the files only other sessions wrote put back at HEAD.
///             If it is still there, S's edits cause it on their own, and S is told.
///         </item>
///         <item>
///             Otherwise it is bound in a view with the files only S wrote put back at HEAD. If it is still there, other
///             sessions' edits cause it without S, and S is not told. If it is gone, it needs both, and S is told.
///         </item>
///     </list>
///     <para>
///         An error in a file S wrote together with another session is always told. Each view is derived once per check
///         from the current solution, and only the files of errors that need a decision are bound in it, up to
///         <see cref="MaxDecidedFiles"/> files; an error past that is told, which is what a check without attribution does.
///     </para>
/// </remarks>
internal sealed class SessionAttribution
{
    /// <summary>At most this many files are bound in the views per check; the errors in further files are told.</summary>
    public const int MaxDecidedFiles = 40;

    private readonly RepoWorkspace _workspace;
    private readonly IntroducedErrors _introduced;
    private readonly SessionEdits _edits;

    public SessionAttribution(RepoWorkspace workspace, IntroducedErrors introduced, SessionEdits edits)
    {
        _workspace = workspace;
        _introduced = introduced;
        _edits = edits;
    }

    /// <summary>
    ///     The errors of <paramref name="errors"/> that <paramref name="session"/> is told about, in the same order, or null
    ///     when no file that differs from HEAD was written only by other sessions, so every error is told and nothing was
    ///     bound again.
    /// </summary>
    /// <param name="errors">Every error the check found, before the cap on reported errors.</param>
    /// <param name="session">The session the check is answered to.</param>
    /// <param name="cancellationToken">Cancels the binding.</param>
    public async Task<List<CompilerError>?> ToldAsync(IReadOnlyList<CompilerError> errors, string session, CancellationToken cancellationToken)
    {
        var writers = _edits.WritersOf(_workspace.Tracker.Changed);
        var onlyOthers = writers.Where(w => !w.Value.Contains(session)).Select(w => w.Key).ToList();
        // With no file that only other sessions wrote, every error is this session's or everyone's.
        if (errors.Count == 0 || onlyOthers.Count == 0)
            return null;
        var onlyThis = writers.Where(w => w.Value.Count == 1 && w.Value.Contains(session)).Select(w => w.Key).ToList();

        Solution? withoutOthers = null;
        Solution? withoutThis = null;
        var notTold = new HashSet<CompilerError>();
        var decided = 0;
        var root = _workspace.Root;
        foreach (var file in errors.GroupBy(e => e.Path, StringComparer.Ordinal))
        {
            var path = root.PathOf(file.Key);
            if (writers.TryGetValue(path, out var sessions) && sessions.Count > 1 && sessions.Contains(session))
                continue;
            if (decided++ >= MaxDecidedFiles)
                continue;

            withoutOthers ??= await _workspace.CurrentWithHeadContentAsync(onlyOthers, cancellationToken).ConfigureAwait(false);
            var alone = Keys(await _introduced.InFileOfAsync(withoutOthers, path, cancellationToken).ConfigureAwait(false));
            var undecided = file.Where(e => !alone.Contains(Key(e))).ToList();
            if (undecided.Count == 0)
                continue;

            withoutThis ??= await _workspace.CurrentWithHeadContentAsync(onlyThis, cancellationToken).ConfigureAwait(false);
            var byOthers = Keys(await _introduced.InFileOfAsync(withoutThis, path, cancellationToken).ConfigureAwait(false));
            notTold.UnionWith(undecided.Where(e => byOthers.Contains(Key(e))));
        }

        return [.. errors.Where(e => !notTold.Contains(e))];
    }

    /// <summary>An error as <see cref="ErrorDelta"/> matches it: file, id and message, without the position an edit moves.</summary>
    private static (string Path, string Id, string Message) Key(CompilerError error) => (error.Path, error.Id, error.Message);

    private static HashSet<(string Path, string Id, string Message)> Keys(IEnumerable<CompilerError> errors) => [.. errors.Select(Key)];
}
