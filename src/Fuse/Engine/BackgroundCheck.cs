using Fuse.Paths;

namespace Fuse.Engine;

/// <summary>
///     The background re-check of the files each live session wrote: after a generation is published it runs,
///     debounced and one session at a time through the router's background lane, for every session whose files
///     the newest generation holds since its last background check, and the router stores each answer by its
///     content key and as that session's file. A newer generation cancels the run it supersedes, so background
///     work on a stale generation stops instead of storing an answer the tree has already left.
/// </summary>
internal sealed class BackgroundCheck : IDisposable
{
    /// <summary>How long schedules gather before a run starts, so a burst of edits is one re-check per session.</summary>
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);

    /// <summary>The most sessions a run remembers it checked; past it the memory goes and a later run checks a session again.</summary>
    private const int MaxCheckedSessions = 128;

    private readonly Func<IReadOnlyList<SessionFiles>> _sessions;
    private readonly Func<SessionFiles, string?> _keyFor;
    private readonly AnswerStore _answers;
    private readonly Func<SessionFiles, CancellationToken, Task> _check;
    private readonly Action<string> _log;
    private readonly TimeSpan _debounce;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (long Generation, string Files)> _checked = new(StringComparer.Ordinal);
    private long _pending = -1;
    private long _running = -1;
    private DateTimeOffset _scheduledAt;
    private CancellationToken _shutdown;
    private CancellationTokenSource? _runSource;
    private Task? _runner;
    private bool _disposed;

    /// <param name="sessions">The live sessions and the files each wrote in the newest generation, read without a sync.</param>
    /// <param name="keyFor">The content key a check of one session's files coalesces and is stored by, or null when it keeps none.</param>
    /// <param name="results">The store the router keeps each answer in; a session whose key it already holds is not checked again.</param>
    /// <param name="check">Runs one session's re-check in the background lane and stores its answer; cancelled by a newer generation.</param>
    /// <param name="log">Where a run that fails is written.</param>
    /// <param name="debounce">How long schedules gather before a run starts; tests name a shorter one.</param>
    public BackgroundCheck(
        Func<IReadOnlyList<SessionFiles>> sessions,
        Func<SessionFiles, string?> keyFor,
        AnswerStore results,
        Func<SessionFiles, CancellationToken, Task> check,
        Action<string> log,
        TimeSpan? debounce = null)
    {
        _sessions = sessions;
        _keyFor = keyFor;
        _answers = results;
        _check = check;
        _log = log;
        _debounce = debounce ?? DefaultDebounce;
    }

    /// <summary>The live sessions a run has re-checked, for a test that waits for a run to cover a session.</summary>
    internal int CheckedCount
    {
        get
        {
            lock (_lock)
                return _checked.Count;
        }
    }

    /// <summary>
    ///     Asks for a re-check after <paramref name="generation"/>: schedules gather for the debounce, and a
    ///     generation newer than the one a run holds cancels that run, whose sessions the new run covers.
    /// </summary>
    /// <param name="shutdown">The engine's shutdown token, which stops every run.</param>
    public void Schedule(long generation, CancellationToken shutdown)
    {
        CancellationTokenSource? cancel = null;
        lock (_lock)
        {
            if (_disposed)
                return;
            _shutdown = shutdown;
            if (generation <= _running && _runner is { IsCompleted: false })
                return;
            if (generation > _pending)
                _pending = generation;
            _scheduledAt = DateTimeOffset.UtcNow;
            if (generation > _running && _runSource is not null)
                cancel = _runSource;
            if (_runner is null || _runner.IsCompleted)
                _runner = Task.Run(RunAsync, CancellationToken.None);
        }

        cancel?.Cancel();
    }

    /// <summary>Stops the run that is going and drops the one that is scheduled, when the memory budget sheds background work at a request boundary.</summary>
    public void CancelCurrent()
    {
        CancellationTokenSource? cancel = null;
        lock (_lock)
        {
            _pending = -1;
            cancel = _runSource;
        }

        cancel?.Cancel();
    }

    /// <summary>Waits for the scheduled runs to drain, after the shutdown token has been cancelled, so no run checks into a disposed workspace.</summary>
    public async Task WaitAsync()
    {
        Task? runner;
        lock (_lock)
            runner = _runner;
        if (runner is not null)
            await runner.ConfigureAwait(false);
    }

    public void Dispose()
    {
        CancellationTokenSource? cancel = null;
        lock (_lock)
        {
            _disposed = true;
            _pending = -1;
            cancel = _runSource;
        }

        cancel?.Cancel();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            long generation;
            CancellationTokenSource source;
            lock (_lock)
            {
                if (_disposed || _pending < 0)
                {
                    _runner = null;
                    return;
                }
            }

            if (DateTimeOffset.UtcNow - _scheduledAt < _debounce)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(20), _shutdown).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            lock (_lock)
            {
                if (_disposed || _pending < 0)
                {
                    _runner = null;
                    return;
                }

                generation = _pending;
                _pending = -1;
                _running = generation;
                _runSource = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
                source = _runSource;
            }

            try
            {
                foreach (var session in _sessions())
                {
                    source.Token.ThrowIfCancellationRequested();
                    var files = string.Join(",", session.Files.Select(f => f.Relative).Order(StringComparer.Ordinal));
                    lock (_lock)
                    {
                        if (_checked.TryGetValue(session.Session, out var last) && last.Generation >= session.Generation && last.Files == files)
                            continue;
                    }

                    var key = _keyFor(session);
                    if (key is not null && _answers.TryGet(key, out _))
                    {
                        MarkChecked(session, files);
                        continue;
                    }

                    await _check(session, source.Token).ConfigureAwait(false);
                    MarkChecked(session, files);
                }
            }
            catch (OperationCanceledException) when (source.IsCancellationRequested)
            {
                // A newer generation cancelled this run and is already scheduled; the loop covers its sessions.
            }
            catch (Exception e)
            {
                _log($"background check failed: {e.Message}");
            }
            finally
            {
                lock (_lock)
                {
                    if (_running == generation)
                        _running = -1;
                    if (ReferenceEquals(_runSource, source))
                        _runSource = null;
                }

                source.Dispose();
            }
        }
    }

    private void MarkChecked(SessionFiles session, string files)
    {
        lock (_lock)
        {
            if (!_checked.ContainsKey(session.Session) && _checked.Count >= MaxCheckedSessions)
                _checked.Clear();
            _checked[session.Session] = (session.Generation, files);
        }
    }

    /// <summary>One live session and the files it wrote in one generation: its name as checks answer to it, that generation, and its files in the order the router checks them.</summary>
    internal sealed record SessionFiles(string Session, long Generation, IReadOnlyList<RepoPath> Files);
}
