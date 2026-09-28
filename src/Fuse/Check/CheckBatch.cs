using Fuse.Engine;
using Fuse.Protocol;
using Fuse.Repo;

namespace Fuse.Check;

/// <summary>
///     The queue a check request waits in. One engine serves one repository to several agents at once, and today each
///     request is answered in turn: a second agent's check waits a first agent's whole check to finish, including the
///     projects it loads and the dependents it searches. When several requests arrive within a short window the work is
///     nearly the same for all of them, so they are answered together: one sync, one load, one binding pass over the union,
///     and each client its own answer.
///
///     <para>
///         Coalescing must not change what a client is told. Each answer is built from the client's own targets and the
///         candidates the client's own change reaches, so an answer equals what a serial check would have returned for that
///         client alone. A request that arrives after the batch has read the working tree belongs to the next batch.
///     </para>
/// </summary>
internal sealed class CheckBatch
{
    private readonly Checker _checker;
    private readonly TimeSpan _window;
    private readonly object _state = new();
    private readonly List<Waiting> _pending = [];
    private bool _running;

    public CheckBatch(Checker checker, TimeSpan window)
    {
        _checker = checker;
        _window = window;
    }

    /// <summary>The window a batch waits for more requests before it starts.</summary>
    public TimeSpan Window => _window;

    /// <summary>Queues a check and returns the answer the batch gives it.</summary>
    /// <param name="files">The files the client edited, or null for every change since HEAD.</param>
    /// <param name="phases">Collects what this client's share of the batch took, or null to collect nothing.</param>
    /// <param name="cancellationToken">Cancelled when the client goes away; a cancelled client is dropped from its batch.</param>
    public Task<CheckReport> EnqueueAsync(IReadOnlyCollection<string>? files, PhaseTimes? phases, CancellationToken cancellationToken)
    {
        var waiting = new Waiting(files, phases, cancellationToken);
        lock (_state)
        {
            _pending.Add(waiting);
            if (_running)
                return waiting.Answer;
            _running = true;
        }

        _ = RunAsync();
        return waiting.Answer;
    }

    /// <summary>How many clients the last batch answered, for the engine log.</summary>
    public int LastBatchSize => _lastBatchSize;

    private int _lastBatchSize;

    private async Task RunAsync()
    {
        while (true)
        {
            // The window is what turns several requests into one batch; without it a second request arriving a
            // millisecond after the first would start a batch of its own and the work would not be shared.
            await Task.Delay(_window).ConfigureAwait(false);
            List<Waiting> batch;
            lock (_state)
            {
                batch = [.. _pending];
                _pending.Clear();
                if (batch.Count == 0)
                {
                    _running = false;
                    return;
                }
            }

            // A client that disconnected while the window was open is not in the batch, and does not hold it up. The
            // index is kept so each answer goes back to the client it belongs to and not to the one before it.
            var alive = new List<(Waiting Waiting, int Index)>();
            for (var i = 0; i < batch.Count; i++)
            {
                if (!batch[i].Cancellation.IsCancellationRequested)
                    alive.Add((batch[i], i));
            }

            _lastBatchSize = alive.Count;
            IReadOnlyList<CheckReport> reports;
            try
            {
                PhaseTimes?[] collectors = [.. alive.Select(a => a.Waiting.Phases)];
                reports = await _checker.CheckManyAsync([.. alive.Select(a => a.Waiting.Files)], collectors, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _checker.LogBatchFailure(e);
                foreach (var waiting in batch)
                    waiting.Fail(e);
                continue;
            }

            // Each answer goes back to the client it belongs to, and a client that is not in the batch gets nothing.
            var answers = new CheckReport?[batch.Count];
            for (var i = 0; i < alive.Count; i++)
                answers[alive[i].Index] = reports[i];
            for (var i = 0; i < batch.Count; i++)
                batch[i].Complete(answers[i]);
        }
    }

    /// <summary>One queued request and the answer it is waiting for.</summary>
    private sealed class Waiting(IReadOnlyCollection<string>? files, PhaseTimes? phases, CancellationToken cancellationToken)
    {
        private readonly TaskCompletionSource<CheckReport> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyCollection<string>? Files { get; } = files;

        public PhaseTimes? Phases { get; } = phases;

        public CancellationToken Cancellation { get; } = cancellationToken;

        public Task<CheckReport> Answer => _answer.Task;

        /// <summary>Answers the client, or drops it when its answer is null because it went away.</summary>
        public void Complete(CheckReport? report)
        {
            if (report is null || Cancellation.IsCancellationRequested)
                _answer.TrySetCanceled();
            else
                _answer.TrySetResult(report);
        }

        public void Fail(Exception e) => _answer.TrySetException(e);
    }
}
