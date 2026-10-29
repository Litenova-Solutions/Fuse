using System.Diagnostics;
using System.Text;
using Fuse.Paths;
using Fuse.Telemetry;
using Fuse.Testing;
using Fuse.Testing.Model;
using Fuse.Workspace;

namespace Fuse.Engine;

/// <summary>
///     Prepares the shadow runs a later <c>fuse test</c> will ask for, once the tree has settled: when the changed files
///     and HEAD stay the same for <see cref="SettleAfter"/>, it selects the affected tests and emits them, so the ask pays
///     selection and execution but not the mirror and emit. Its plan is never served as an answer, and it runs no test.
/// </summary>
internal sealed class SettledPlanner
{
    /// <summary>How long the tree must stay unchanged before one settled preparation starts.</summary>
    public static readonly TimeSpan SettleAfter = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(1);

    private readonly TestPlanner _planner;
    private readonly RequestGate _gate;
    private readonly RepoWorkspace _workspace;
    private readonly Action<string> _log;
    private readonly TimeSpan _settleAfter;
    private readonly TimeSpan _pollEvery;
    private Task _running = Task.CompletedTask;
    private string? _stable;
    private long _stableSince;
    private string? _prepared;

    /// <param name="planner">The planner a settled preparation selects and emits with.</param>
    /// <param name="gate">The request lock, held briefly while each poll syncs and while the next settled plan is selected.</param>
    /// <param name="workspace">The workspace the tree's snapshot is taken from.</param>
    /// <param name="log">Receives one line per settled preparation.</param>
    public SettledPlanner(TestPlanner planner, RequestGate gate, RepoWorkspace workspace, Action<string> log)
        : this(planner, gate, workspace, log, SettleAfter, PollEvery)
    {
    }

    /// <summary>The settle loop with its intervals, so a test settles at once instead of after five seconds.</summary>
    internal SettledPlanner(TestPlanner planner, RequestGate gate, RepoWorkspace workspace, Action<string> log, TimeSpan settleAfter, TimeSpan pollEvery)
    {
        _planner = planner;
        _gate = gate;
        _workspace = workspace;
        _log = log;
        _settleAfter = settleAfter;
        _pollEvery = pollEvery;
    }

    /// <summary>Starts preparing settled plans in the background until <paramref name="shutdown"/> is cancelled.</summary>
    public void Start(CancellationToken shutdown) => _running = Task.Run(() => RunAsync(shutdown), CancellationToken.None);

    /// <summary>
    ///     Waits for the settle loop to stop, after the shutdown token has been cancelled, so the workspace is never
    ///     disposed under a settled preparation.
    /// </summary>
    public async Task WaitAsync()
    {
        try
        {
            await _running.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException)
        {
        }
    }

    private async Task RunAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollEvery, shutdown).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            string snapshot;
            try
            {
                await _gate.EnterAsync("SettledPoll", shutdown).ConfigureAwait(false);
                List<RepoPath> changed;
                string? head;
                try
                {
                    await _workspace.SyncAsync([], shutdown).ConfigureAwait(false);
                    changed = [.. _workspace.Tracker.Changed];
                    head = _workspace.Tracker.Head;
                }
                finally
                {
                    _gate.Exit();
                }

                snapshot = Snapshot(head, changed);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                // A request is folding changes in while the snapshot is taken; the next poll sees them settled or not.
                _stable = null;
                continue;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log($"settled plan skipped: {e.Message}");
                continue;
            }

            if (snapshot != _stable)
            {
                _stable = snapshot;
                _stableSince = Environment.TickCount64;
                continue;
            }

            if (snapshot == _prepared || Environment.TickCount64 - _stableSince < _settleAfter.TotalMilliseconds)
                continue;

            try
            {
                await PrepareOnceAsync(snapshot, shutdown).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _prepared = snapshot;
                _log($"settled plan skipped: {e.Message}");
            }
        }
    }

    /// <summary>
    ///     Selects the affected tests under the request lock and emits them after it is released, unless the tree moved
    ///     during the selection.
    /// </summary>
    private async Task PrepareOnceAsync(string settled, CancellationToken shutdown)
    {
        var preparing = Stopwatch.StartNew();
        PendingPlan pending;
        await _gate.EnterAsync("SettledPlan", shutdown).ConfigureAwait(false);
        try
        {
            pending = await _planner.SelectAsync(new TestScope.Affected(), PhaseTimes.None, shutdown).ConfigureAwait(false);
            // Noted again after the sync: when it moved during the selection, the emit would warm images for a tree nobody has.
            if (Snapshot(_workspace.Tracker.Head, _workspace.Tracker.Changed) != settled)
                return;
        }
        finally
        {
            _gate.Exit();
        }

        _prepared = settled;
        if (pending.Projects.Count == 0)
            return;
        await _planner.PrepareAsync(pending, PhaseTimes.None, shutdown).ConfigureAwait(false);
        _log($"settled plan: prepared shadow runs for {pending.Projects.Count} test project(s) in {preparing.ElapsedMilliseconds} ms");
    }

    /// <summary>The tree as the settle loop watches it: HEAD and every changed file with its length and write time.</summary>
    private static string Snapshot(string? head, IReadOnlyCollection<RepoPath> changed)
    {
        var builder = new StringBuilder(head ?? "");
        foreach (var path in changed.OrderBy(p => p.Absolute, StringComparer.Ordinal))
        {
            long length = -1;
            long ticks = -1;
            try
            {
                if (File.Exists(path.Absolute))
                {
                    var file = new FileInfo(path.Absolute);
                    length = file.Length;
                    ticks = file.LastWriteTimeUtc.Ticks;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            builder.Append('\n').Append(path.Absolute).Append('=').Append(length).Append(',').Append(ticks);
        }

        return builder.ToString();
    }
}
