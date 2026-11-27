using System.Collections.Concurrent;
using System.Diagnostics;
using Fuse.Changes;
using Fuse.Changes.Model;
using Fuse.Check.Analyzers;
using Fuse.Check.Model;
using Fuse.Check.Pipeline;
using Fuse.Check.Sessions;
using Fuse.Engine.Background;
using Fuse.Engine.Server;
using Fuse.Failures;
using Fuse.Identity;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Check;
using Fuse.Protocol.Requests;
using Fuse.Repo.Tracking;
using Fuse.Telemetry;
using Fuse.Workspace.Generations;
using Fuse.Workspace.Views;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
namespace Fuse.Engine.Routing;

/// <summary>
///     Answers the check requests: checks of every change, of named files and of named paths. It takes each check's
///     content key, serves and keeps answers by it, and names the live sessions the background re-check runs.
/// </summary>
internal sealed class CheckRoute
{
    private readonly RepoWorkspace _workspace;
    private readonly Checker _checker;
    private readonly SessionEdits _sessions;
    private readonly SurfaceGuardStore _guards;
    private readonly WatchHub? _watchHub;
    private readonly EngineLog _log;
    private readonly RepoRoot _root;
    private readonly AnswerStore _answers;
    private readonly RequestLog _requestLog;

    internal CheckRoute(RepoWorkspace workspace, Checker checker, SessionEdits sessions, SurfaceGuardStore guards, WatchHub? watchHub, EngineLog log, RepoRoot root, AnswerStore answers, RequestLog requestLog)
    {
        _workspace = workspace;
        _checker = checker;
        _sessions = sessions;
        _guards = guards;
        _watchHub = watchHub;
        _log = log;
        _root = root;
        _answers = answers;
        _requestLog = requestLog;
    }

    /// <summary>Checks every change, answered to the session the request names, with a tree stamp.</summary>
    internal async Task<EngineResponse> CheckChangesAsync(EngineRequest.CheckChanges changes, AnalyzerRun analyzers, PhaseTimes phases, CancellationToken cancellationToken)
    {
        var session = SessionId.IsValid(changes.Session) ? changes.Session : null;
        var options = new CheckOptions(IncludeRepairBrief: changes.Brief);
        var answered = Stamped(ResponseMapper.Answered(await _checker.CheckAsync(new CheckScope.AllChanges(), session, analyzers, phases, options, cancellationToken, maxReported: changes.MaxErrors).ConfigureAwait(false)), stamp: true);
        await PublishGuardViolationsAsync(cancellationToken).ConfigureAwait(false);
        return answered;
    }

    /// <summary>
    ///     Checks the files <paramref name="check"/> names. The wire carries them as strings, absolute or relative to the
    ///     root, in whatever letter case the client wrote; each becomes a <see cref="RepoPath"/> in the spelling the file
    ///     system holds it under, and one that is empty or not a valid path is answered with
    ///     <see cref="Fuse.Failures.ErrorCode.InvalidPath"/>, naming it, before anything is checked.
    /// </summary>
    internal async Task<EngineResponse> CheckFilesAsync(EngineRequest.CheckFiles check, AnalyzerRun analyzers, PhaseTimes phases, CancellationToken cancellationToken)
    {
        var paths = new List<RepoPath>(check.Files.Count);
        foreach (var file in check.Files)
        {
            if (string.IsNullOrWhiteSpace(file))
                return new EngineResponse.Unanswered(ErrorCode.InvalidPath, ErrorMessages.EmptyPath);
            try
            {
                paths.Add(_workspace.Root.PathOfNamed(file));
            }
            catch (Exception e) when (e is ArgumentException or PathTooLongException)
            {
                return new EngineResponse.Unanswered(ErrorCode.InvalidPath, ErrorMessages.InvalidPath(file));
            }
        }

        var session = SessionId.IsValid(check.Session) ? check.Session : null;
        var options = new CheckOptions(check.InteractiveFast, check.Brief);
        var answered = Stamped(ResponseMapper.Answered(await _checker.CheckAsync(new CheckScope.Files(paths), session, analyzers, phases, options, cancellationToken, maxReported: check.MaxErrors).ConfigureAwait(false)), stamp: true);
        await PublishGuardViolationsAsync(cancellationToken).ConfigureAwait(false);
        return answered;
    }

    /// <summary>
    ///     Checks the paths <paramref name="check"/> names. A path that is empty is answered with
    ///     <see cref="Fuse.Failures.ErrorCode.InvalidPath"/> before anything is checked; the checker expands the rest.
    /// </summary>
    internal async Task<EngineResponse> CheckPathsAsync(EngineRequest.CheckPaths check, AnalyzerRun analyzers, PhaseTimes phases, CancellationToken cancellationToken)
    {
        foreach (var spec in check.Paths)
        {
            if (string.IsNullOrWhiteSpace(spec))
                return new EngineResponse.Unanswered(ErrorCode.InvalidPath, ErrorMessages.EmptyPath);
        }

        var options = new CheckOptions(IncludeRepairBrief: check.Brief);
        var answered = Stamped(ResponseMapper.Answered(await _checker.CheckAsync(new CheckScope.NamedPaths(check.Paths), SessionId.IsValid(check.Session) ? check.Session : null, analyzers, phases, options, cancellationToken, maxReported: check.MaxErrors).ConfigureAwait(false)), stamp: true);
        await PublishGuardViolationsAsync(cancellationToken).ConfigureAwait(false);
        return answered;
    }

    /// <summary>The kept answer to <paramref name="request"/> shaped for it, or null when its content key keeps nothing.</summary>
    internal EngineResponse? TryAnswerKept(EngineRequest request, string key)
    {
        if (!_answers.TryGet(key, out var stored))
            return null;
        var phases = new PhaseTimes();
        phases.Add(Phase.Gate, 0);
        _requestLog.Write(request, phases, 0, null);
        return ForRequest(request, new EngineResponse.CheckAnswered(stored));
    }

    /// <summary>
    ///     Credits the session a request carries with the files it names and marks a stopping session stopped.
    ///     Recorded before anything can refuse the check, so a check answered "still loading" still credits the
    ///     session with its files, and before a kept answer is served, so its record is the one that key held.
    /// </summary>
    internal void NoteSession(EngineRequest request)
    {
        if (request is EngineRequest.CheckFiles { Session: { } writer } written && SessionId.IsValid(writer))
            RecordSession(writer, written.Files);
        if (request is EngineRequest.CheckChanges { Mode: SessionCheckMode.Stop, Session: { } stopping } && SessionId.IsValid(stopping))
            _sessions.MarkStopped(stopping);
    }

    /// <summary>
    ///     The files a check names for the sync that runs before its content key is taken, so the key holds what is
    ///     on disk now: a files check its files, a paths check its recently written sources, and a check of every
    ///     change none. A name that is not a valid path is left to the check to refuse.
    /// </summary>
    internal List<RepoPath> KnownPathsOf(EngineRequest request)
    {
        if (request is EngineRequest.CheckPaths paths)
            return ScopeExpansion.RecentlyWritten(_workspace.Root, paths.Paths);
        if (request is not EngineRequest.CheckFiles files)
            return [];
        var known = new List<RepoPath>(files.Files.Count);
        foreach (var file in files.Files)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(file))
                    known.Add(_workspace.Root.PathOfNamed(file));
            }
            catch (Exception e) when (e is ArgumentException or PathTooLongException)
            {
                // CheckFilesAsync answers this file with InvalidPath.
            }
        }

        return known;
    }

    /// <summary>
    ///     The content key a check coalesces by with the generation it was taken from, or nulls when it does not
    ///     coalesce: its scope, session, analyzer mode and the generation content hash the kept answer keys by. A
    ///     tree past the kept-answer bound, or a changed file that cannot be read, coalesces nothing.
    /// </summary>
    internal (string? Key, Generation? Pinned) CoalesceKey(EngineRequest request)
    {
        string scopeKey;
        string? session;
        bool compilationWide;
        int maxErrors;
        switch (request)
        {
            case EngineRequest.CheckChanges changes:
                scopeKey = "all" + (changes.Brief ? "|brief" : "");
                session = SessionId.IsValid(changes.Session) ? changes.Session : null;
                compilationWide = true;
                maxErrors = changes.MaxErrors;
                break;
            case EngineRequest.CheckFiles files:
                scopeKey = "files:" + string.Join(",", files.Files.Order(StringComparer.Ordinal)) + (files.Brief ? "|brief" : "") + (files.InteractiveFast ? "|fast" : "");
                session = SessionId.IsValid(files.Session) ? files.Session : null;
                compilationWide = !files.LeaveOutCompilationWide;
                maxErrors = files.MaxErrors;
                break;
            case EngineRequest.CheckPaths paths:
                scopeKey = "paths:" + string.Join(",", paths.Paths.Order(StringComparer.Ordinal)) + (paths.Brief ? "|brief" : "");
                session = SessionId.IsValid(paths.Session) ? paths.Session : null;
                compilationWide = true;
                maxErrors = paths.MaxErrors;
                break;
            default:
                return (null, null);
        }

        Generation generation;
        try
        {
            generation = _workspace.PinGeneration();
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            return (null, null);
        }

        if (generation.Changed.Count > KeptCheck.MaxChangedFiles)
            return (null, null);
        var kept = KeptCheck.KeyFor(
            scopeKey,
            maxErrors,
            session,
            compilationWide,
            generation.Head ?? "",
            generation.ConfigurationGeneration,
            generation.BaselineGeneration,
            _sessions.RecordsHash(),
            generation.Changed.Select(path => (path.Relative, path.Absolute)));
        // The stamp a request asks for is no part of the key: it names the same errors, and each waiter of a
        // shared check is stamped, or not, for itself when it is answered.
        return kept is null ? (null, null) : ($"{scopeKey}|{session ?? ""}|{compilationWide}|{kept}", generation);
    }

    /// <summary>
    ///     A kept or shared answer as <paramref name="request"/> asked for it: with the tree the check stamped it
    ///     with when the request asked for a stamp, and without one when it did not. A kept answer carries no tree,
    ///     so one that asked for a stamp is stamped from the newest generation, whose content matches it by its key.
    ///     The errors a stamp names are the same either way.
    /// </summary>
    internal EngineResponse ForRequest(EngineRequest request, EngineResponse response)
    {
        if (response is not EngineResponse.CheckAnswered answered)
            return response;
        var stamp = request switch
        {
            EngineRequest.CheckChanges changes => changes.StampTree,
            EngineRequest.CheckFiles files => files.StampTree,
            EngineRequest.CheckPaths paths => paths.StampTree,
            _ => false,
        };
        if (stamp)
            return answered.Report.Tree is null ? Stamped(response, stamp: true) : response;
        return answered.Report.Tree is null
            ? response
            : new EngineResponse.CheckAnswered(answered.Report with { Tree = null, TreeFiles = 0 });
    }

    /// <summary>Keeps a check answer by its content key, as the session file of the session it was answered to, and publishes the session's answer when it changed.</summary>
    /// <param name="keyed">The generation the answer's key was taken from: the check answers from it or from a newer one it synced itself, so the kept and published generation never runs ahead of the errors.</param>
    internal void StoreAnswer(string key, EngineRequest request, EngineResponse response, Generation keyed)
    {
        if (response is not EngineResponse.CheckAnswered answered)
            return;
        var tree = answered.Report.Tree ?? TreeStamp.Of(ChangedPairs(keyed.Changed));
        var session = RequestLog.SessionOf(request);
        CheckReport? previous = null;
        if (session is not null && _answers.TryGetSession(session, out var kept))
            previous = kept;
        _answers.Keep(key, session, keyed.Number, tree, answered.Report);
        if (session is not null && _watchHub is not null && AnswerChanged(previous, answered.Report))
            _watchHub.Publish(new WatchEvent.CheckUpdated
            {
                Session = session,
                WorktreeRoot = _root.Path,
                Generation = keyed.Number,
                Tree = tree,
                ErrorCount = answered.Report.ErrorCount,
                Errors = answered.Report.Errors,
            });
    }

    /// <summary>
    ///     The live sessions of the newest generation and the files each wrote in it, from its writers without a
    ///     sync: a session with no changed file in it is not re-checked. The generation may predate a credit a
    ///     kept answer recorded, so this read drops no credit; only a sync that verifies a file matches HEAD does.
    /// </summary>
    internal IReadOnlyList<BackgroundCheck.SessionFiles> LiveSessionFiles()
    {
        var generation = _workspace.PinGeneration();
        var bySession = new Dictionary<string, List<RepoPath>>(StringComparer.Ordinal);
        foreach (var (path, sessions) in _sessions.WritersOf(generation.Changed))
        {
            foreach (var session in sessions)
            {
                if (!bySession.TryGetValue(session, out var files))
                    bySession[session] = files = [];
                files.Add(path);
            }
        }

        return [.. bySession.OrderBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => new BackgroundCheck.SessionFiles(s.Key, generation.Number, [.. s.Value.OrderBy(f => f.Relative, StringComparer.Ordinal)]))];
    }

    /// <summary>The request a background re-check of <paramref name="session"/> sends: its files, answered to it, with every analyzer and a tree stamp.</summary>
    internal static EngineRequest.CheckFiles BackgroundRequest(BackgroundCheck.SessionFiles session) =>
        new EngineRequest.CheckFiles([.. session.Files.Select(f => f.Absolute)], WaitForLoad: true) { Session = session.Session, StampTree = true };

    /// <summary>The content key a background re-check of <paramref name="session"/> coalesces and is stored by.</summary>
    internal string? BackgroundKey(BackgroundCheck.SessionFiles session) => CoalesceKey(BackgroundRequest(session)).Key;

    /// <summary>
    ///     The check answer with its own tree stamp, from the changed set the check just synced, so the client does
    ///     not ask git again and the store and the published event name the tree the errors were found on. Every
    ///     fresh answer is stamped; the answer is shaped for the request afterwards, which strips the stamp when the
    ///     request asked for none. Unchanged when stamp is false or the check was not answered.
    /// </summary>
    private EngineResponse Stamped(EngineResponse response, bool stamp)
    {
        if (!stamp || response is not EngineResponse.CheckAnswered answered)
            return response;
        var generation = _workspace.PinGeneration();
        var changed = generation.Changed;
        return new EngineResponse.CheckAnswered(answered.Report with { Tree = TreeStamp.Of(ChangedPairs(changed)), TreeFiles = changed.Count });
    }

    /// <summary>What the tree stamp hashes: each changed file with its path relative to the root and its absolute path to read.</summary>
    internal static IEnumerable<(string Relative, string Absolute)> ChangedPairs(IReadOnlyCollection<RepoPath> changed) =>
        changed.Select(path => (path.Relative, path.Absolute));

    /// <summary>True when <paramref name="report"/> answers differently than <paramref name="previous"/>: a first answer, or different errors.</summary>
    private static bool AnswerChanged(CheckReport? previous, CheckReport report)
    {
        if (previous is null)
            return true;
        if (previous.ErrorCount != report.ErrorCount || previous.Errors.Length != report.Errors.Length)
            return true;
        for (var i = 0; i < previous.Errors.Length; i++)
        {
            if (previous.Errors[i].Error.ToString() != report.Errors[i].Error.ToString())
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Publishes a watch event for every frozen declaration the tree changed: after each check, so a watcher learns
    ///     of a violation with the answer that found the edit. Never fails the check it follows.
    /// </summary>
    private async Task PublishGuardViolationsAsync(CancellationToken cancellationToken)
    {
        if (_watchHub is null)
            return;
        try
        {
            var generation = _workspace.PinGeneration();
            if (generation.Changed.Count == 0)
                return;
            var declarations = await ChangedDeclarationsAsync(generation, cancellationToken).ConfigureAwait(false);
            foreach (var violation in _guards.Violations(generation.Changed, declarations))
                _watchHub.Publish(violation);
        }
        catch (Exception e)
        {
            // A cancelled client disconnects; anything else is logged. Either way the check it follows still answers.
            if (e is not OperationCanceledException)
                _log.Write($"guard violations skipped: {e.Message}");
        }
    }

    /// <summary>
    ///     The changed declaration headers of each changed C# file in <paramref name="generation"/>: the working-tree
    ///     header of an added or changed declaration, the HEAD header of a removed one, across every project that
    ///     compiles the file. A file that cannot be read reports no declarations.
    /// </summary>
    private async Task<Dictionary<RepoPath, IReadOnlyList<string>>> ChangedDeclarationsAsync(Generation generation, CancellationToken cancellationToken)
    {
        var declarations = new ConcurrentDictionary<RepoPath, IReadOnlyList<string>>();
        await Parallel.ForEachAsync(
            generation.Changed.Where(path => path.Absolute.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            async (path, ct) =>
            {
                try
                {
                    if (await DeclarationsOfAsync(path, generation, ct).ConfigureAwait(false) is { Count: > 0 } headers)
                        declarations[path] = headers;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _log.Write($"guard violations skipped for {path.Relative}: {e.Message}");
                }
            }).ConfigureAwait(false);
        return new Dictionary<RepoPath, IReadOnlyList<string>>(declarations);
    }

    /// <summary>The changed declaration headers of <paramref name="path"/>, or null when its surface reads the same as HEAD.</summary>
    private async Task<IReadOnlyList<string>?> DeclarationsOfAsync(RepoPath path, Generation generation, CancellationToken cancellationToken)
    {
        var headers = new HashSet<string>(StringComparer.Ordinal);
        var baseline = generation.Baseline;
        var current = generation.Current;
        var projectIds = baseline.GetDocumentIdsWithFilePath(path.Absolute).Concat(current.GetDocumentIdsWithFilePath(path.Absolute)).Select(id => id.ProjectId).Distinct().ToList();
        if (projectIds.Count == 0)
        {
            var before = _workspace.HeadText(path) is { } head ? FileDeclarations.Of(await ParseAsync(head, cancellationToken).ConfigureAwait(false)) : FileDeclarations.Empty;
            var after = await DiskDeclarationsAsync(path, cancellationToken).ConfigureAwait(false);
            AddHeaders(headers, SurfaceDiff.Compare(before, after).Changes);
        }
        else
        {
            foreach (var projectId in projectIds)
            {
                var before = await DeclarationsInAsync(baseline, projectId, path, cancellationToken).ConfigureAwait(false);
                var after = await DeclarationsInAsync(current, projectId, path, cancellationToken).ConfigureAwait(false);
                AddHeaders(headers, SurfaceDiff.Compare(before, after).Changes);
            }
        }

        return headers.Count == 0 ? null : [.. headers.Order(StringComparer.Ordinal)];
    }

    /// <summary>The declarations <paramref name="solution"/> holds for <paramref name="path"/> in <paramref name="projectId"/>, or none when it holds no document there.</summary>
    private static async Task<FileDeclarations> DeclarationsInAsync(Solution solution, ProjectId projectId, RepoPath path, CancellationToken cancellationToken)
    {
        var document = solution.GetDocumentIdsWithFilePath(path.Absolute).Where(id => id.ProjectId == projectId).Select(solution.GetDocument).FirstOrDefault(d => d is not null);
        return document is not null && await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is { } root ? FileDeclarations.Of(root) : FileDeclarations.Empty;
    }

    /// <summary>The declarations of <paramref name="path"/> as the working tree holds it, or none when it is gone.</summary>
    private static async Task<FileDeclarations> DiskDeclarationsAsync(RepoPath path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path.Absolute))
            return FileDeclarations.Empty;
        var text = SolutionViews.Decode(SharedRead.AllBytes(path.Absolute));
        return FileDeclarations.Of(await ParseAsync(text, cancellationToken).ConfigureAwait(false));
    }

    private static Task<SyntaxNode> ParseAsync(SourceText text, CancellationToken cancellationToken) =>
        CSharpSyntaxTree.ParseText(text, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), cancellationToken: cancellationToken)
            .GetRootAsync(cancellationToken);

    /// <summary>Adds the header of each change: the working-tree header of an added or changed declaration, the HEAD header of a removed one.</summary>
    private static void AddHeaders(HashSet<string> headers, IReadOnlyList<DeclarationChange> changes)
    {
        foreach (var change in changes)
        {
            headers.Add(change switch
            {
                DeclarationChange.Added added => added.After,
                DeclarationChange.Changed edited => edited.After,
                DeclarationChange.Removed removed => removed.Before,
                _ => throw new UnreachableException($"a declaration change is added, changed or removed, not {change.GetType().Name}"),
            });
        }
    }

    /// <summary>Takes the request's content key with the generation it was taken from, syncing a check first so the key holds what is on disk now.</summary>
    internal async Task<(string? Key, Generation? Pinned)> PinKeyAsync(EngineRequest request, Task? initialization, CancellationToken cancellationToken)
    {
        var isCheck = request is EngineRequest.CheckChanges or EngineRequest.CheckFiles or EngineRequest.CheckPaths;
        if (isCheck && initialization is not { IsCompleted: false })
        {
            // A check answers for the files on disk, not for the tree the watcher last reported: a write it
            // has not reported yet is in no generation yet, and the kept answer of the tree without it would
            // answer for that tree (and stamp it). The sync confirms the changed set against disk before any
            // kept answer is served, and the key is taken from the generation it publishes. A check that
            // names files syncs them by name, so the key holds what the hook just wrote; the check itself
            // syncing them later would compute the right answer but keep it under this stale key. A check of
            // every change names none. Only macOS confirms with git: its file watcher can report a write
            // late, while Windows and Linux report it before another process can ask, and git costs tens of
            // milliseconds on a large repository.
            try
            {
                await _workspace.SyncAndPinAsync(KnownPathsOf(request), cancellationToken, confirmWithDisk: OperatingSystem.IsMacOS()).ConfigureAwait(false);
                return CoalesceKey(request);
            }
            catch (FuseException)
            {
                // The check below surfaces this refusal; no kept answer is served for a tree that could
                // not be confirmed.
                return (null, null);
            }
        }

        // The key is taken once, from the request and the generation it pins: a check takes it after
        // its sync above, from the generation that sync published, and no request reads and hashes
        // every changed file twice before its phase timer starts.
        return CoalesceKey(request);
    }

    /// <summary>Records that <paramref name="session"/> wrote <paramref name="files"/>. A name that is not a valid path is left to the check to refuse.</summary>
    private void RecordSession(string session, IReadOnlyList<string> files)
    {
        var paths = new List<RepoPath>(files.Count);
        foreach (var file in files)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(file))
                    paths.Add(_workspace.Root.PathOfNamed(file));
            }
            catch (Exception e) when (e is ArgumentException or PathTooLongException)
            {
                // CheckFilesAsync answers this file with InvalidPath.
            }
        }

        _sessions.Record(session, paths);
        // The next sync takes these files whoever runs it, so a credit is never forgotten as if its file matched HEAD
        // because an answer was served from the result store before the watcher reported the write.
        _workspace.Tracker.ReportWritten(paths);
    }
}
