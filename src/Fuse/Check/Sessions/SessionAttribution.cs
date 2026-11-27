using System.Collections.Concurrent;
using Fuse.Check.Diagnostics;
using Fuse.Check.Model;
using Fuse.Check.Pipeline;
using Fuse.Paths;
using Fuse.Workspace.Generations;
using Fuse.Workspace.Views;
using Microsoft.CodeAnalysis;
namespace Fuse.Check.Sessions;

/// <summary>
///     Decides which of a check's errors a session is told about, when several sessions write one working tree. The check
///     itself is unchanged: it reads the whole tree against HEAD. Attribution only leaves out, for one session, the errors
///     other sessions' edits caused without it.
/// </summary>
/// <remarks>
///     <para>
///         S's own edits are its own and those of its subagents that have stopped (<see cref="SessionEdits.IsOwnEdit"/>);
///         a subagent that is still running counts as another session. A session that has stopped is settled: its edits
///         stay in every view, as the edits of a session that has finished do, so an error they cause is everyone's. The
///         files that differ from HEAD fall into three groups for session S: files only S wrote, files only other live
///         sessions wrote, and the rest, which are files several sessions wrote, files a settled session wrote and files
///         no session is credited with because a shell command, a generator or a tool Fuse never saw changed them. The
///         rest stay in every view, so an error they cause is everyone's.
///     </para>
///     <para>
///         A declaration filter runs first (<see cref="DeclarationFilter"/>): an error whose bound symbol, and every
///         candidate symbol, traces to declarations exactly one session changed is decided from that trace, and the
///         filter declines the rest. For each remaining error in a file S did not write together with another session:
///     </para>
///     <list type="number">
///         <item>
///             It is bound again in S's session view: the working tree with the files only other live sessions wrote put
///             back at HEAD. If it is still there, S's edits cause it on their own, and S is told.
///         </item>
///         <item>
///             Otherwise it is bound in a view with the files only S wrote put back at HEAD. If it is still there, other
///             sessions' edits cause it without S, and S is not told. If it is gone, it needs both, and S is told.
///         </item>
///     </list>
///     <para>
///         An error in a file S wrote together with another session is always told. Each view is the view the writer
///         keeps for its files at HEAD (see <see cref="SolutionViews"/>),
///         warm from the checks before it, and only the files of errors that need a decision are bound in it, up to
///         <see cref="MaxDecidedFiles"/> files; an error past that is told, which is what a check without attribution does.
///         A session whose view the writer shed derives it from the current view again and is answered the same.
///     </para>
/// </remarks>
internal sealed class SessionAttribution
{
    /// <summary>At most this many files are bound in the views per check; the errors in further files are told.</summary>
    public const int MaxDecidedFiles = 40;

    private readonly RepoWorkspace _workspace;
    private readonly IntroducedErrors _introduced;
    private readonly SessionEdits _edits;
    private readonly DeclarationFilter _filter;

    public SessionAttribution(RepoWorkspace workspace, IntroducedErrors introduced, SessionEdits edits)
    {
        _workspace = workspace;
        _introduced = introduced;
        _edits = edits;
        _filter = new DeclarationFilter(workspace, edits);
    }

    /// <summary>
    ///     The errors of <paramref name="errors"/> that <paramref name="session"/> is told about, in the same order, and how
    ///     many files with errors to decide were past <see cref="MaxDecidedFiles"/>, whose errors are told undecided; or
    ///     null when no file that differs from HEAD was written only by other live sessions, so every error is told and
    ///     nothing was bound again.
    /// </summary>
    /// <param name="errors">Every error the check found, before the cap on reported errors.</param>
    /// <param name="session">The session the check is answered to.</param>
    /// <param name="cancellationToken">Cancels the binding.</param>
    public Task<(List<CompilerError> Told, int FilesNotAttributed)?> ToldAsync(IReadOnlyList<CompilerError> errors, string session, CancellationToken cancellationToken) =>
        ToldAsync(errors, session, _workspace.PinGeneration(), new CheckContext(AnalyzerRun.Full, reuse: true), cancellationToken);

    /// <summary>The errors of <paramref name="errors"/> that <paramref name="session"/> is told about in <paramref name="generation"/> for <paramref name="context"/>.</summary>
    public async Task<(List<CompilerError> Told, int FilesNotAttributed)?> ToldAsync(IReadOnlyList<CompilerError> errors, string session, Generation generation, CheckContext context, CancellationToken cancellationToken)
    {
        var writers = _edits.WritersOf(generation.Changed);
        var onlyOthers = writers.Where(w => IsOnlyLiveOthers(w.Value, session)).Select(w => w.Key).ToList();
        // With no file that only other live sessions wrote, every error is this session's or everyone's.
        if (errors.Count == 0 || onlyOthers.Count == 0)
            return null;
        var onlyThis = writers.Where(w => w.Value.All(s => _edits.IsOwnEdit(s, session))).Select(w => w.Key).ToList();

        var root = _workspace.Root;
        var decidedFiles = new List<(RepoPath Path, List<CompilerError> Errors)>();
        var decided = 0;
        var notAttributed = 0;
        foreach (var file in errors.GroupBy(e => e.Path, StringComparer.Ordinal))
        {
            var path = root.PathOf(file.Key);
            if (writers.TryGetValue(path, out var sessions) && sessions.Any(s => _edits.IsOwnEdit(s, session)) && !sessions.All(s => _edits.IsOwnEdit(s, session)))
                continue;
            if (decided++ >= MaxDecidedFiles)
            {
                notAttributed++;
                continue;
            }

            decidedFiles.Add((path, [.. file]));
        }

        var notTold = new ConcurrentDictionary<CompilerError, byte>();
        using var withoutOthersLock = new SemaphoreSlim(1, 1);
        using var withoutThisLock = new SemaphoreSlim(1, 1);
        Solution? withoutOthers = null;
        Solution? withoutThis = null;

        async Task<Solution> WithoutOthersAsync(CancellationToken ct)
        {
            if (Volatile.Read(ref withoutOthers) is { } ready)
                return ready;
            await withoutOthersLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return withoutOthers ??= await _workspace.CurrentWithHeadContentAsync(generation, onlyOthers, ct).ConfigureAwait(false);
            }
            finally
            {
                withoutOthersLock.Release();
            }
        }

        async Task<Solution> WithoutThisAsync(CancellationToken ct)
        {
            if (Volatile.Read(ref withoutThis) is { } ready)
                return ready;
            await withoutThisLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return withoutThis ??= await _workspace.CurrentWithHeadContentAsync(generation, onlyThis, ct).ConfigureAwait(false);
            }
            finally
            {
                withoutThisLock.Release();
            }
        }

        await Parallel.ForEachAsync(
            decidedFiles,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            async (entry, ct) =>
            {
                var remaining = new List<CompilerError>();
                foreach (var error in entry.Errors)
                {
                    switch (await _filter.DecideAsync(error, session, generation, writers, ct).ConfigureAwait(false))
                    {
                        case DeclarationFilter.Decision.NotTold:
                            notTold.TryAdd(error, 0);
                            break;
                        case DeclarationFilter.Decision.Told:
                            break;
                        default:
                            remaining.Add(error);
                            break;
                    }
                }

                if (remaining.Count == 0)
                    return;

                var alone = Keys(await _introduced.InFileOfAsync(await WithoutOthersAsync(ct).ConfigureAwait(false), entry.Path, generation, context, ct).ConfigureAwait(false));
                var undecided = remaining.Where(e => !alone.Contains(Key(e))).ToList();
                if (undecided.Count == 0)
                    return;

                var byOthers = Keys(await _introduced.InFileOfAsync(await WithoutThisAsync(ct).ConfigureAwait(false), entry.Path, generation, context, ct).ConfigureAwait(false));
                foreach (var error in undecided)
                {
                    if (byOthers.Contains(Key(error)))
                        notTold.TryAdd(error, 0);
                }
            }).ConfigureAwait(false);

        return ([.. errors.Where(e => !notTold.ContainsKey(e))], notAttributed);
    }

    /// <summary>
    ///     True when the sessions that wrote a file are all other sessions than <paramref name="session"/> and at least one
    ///     wrote it. Such a file is put back at HEAD in the session's view.
    /// </summary>
    private bool IsOnlyLiveOthers(IReadOnlySet<string> writersOfFile, string session) =>
        writersOfFile.Count > 0
        && !writersOfFile.Any(s => _edits.IsOwnEdit(s, session));

    /// <summary>An error as <see cref="ErrorDelta"/> matches it: file, id and message, without the position an edit moves.</summary>
    private static (string Path, string Id, string Message) Key(CompilerError error) => (error.Path, error.Id, error.Message);

    private static HashSet<(string Path, string Id, string Message)> Keys(IEnumerable<CompilerError> errors) => [.. errors.Select(Key)];
}
