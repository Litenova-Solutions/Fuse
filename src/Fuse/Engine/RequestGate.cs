using System.Diagnostics;

namespace Fuse.Engine;

/// <summary>
///     The engine's request lock: it admits one holder at a time, and tells each holder what held the lock longest while
///     it waited, so a phase line can say what a long <c>gate</c> wait was spent behind.
/// </summary>
/// <remarks>
///     Every holder names itself on entry: a request by its case (<c>CheckFiles</c>, <c>PlanAffectedTests</c> and so on),
///     the background load as <see cref="Preload"/> and the first evaluation as <see cref="Initialization"/>. When a
///     holder exits, the time it held the lock is added, per waiter, to the part of that waiter's wait it overlapped.
/// </remarks>
internal sealed class RequestGate : IDisposable
{
    /// <summary>The name the background load holds the lock under, while it chooses the next project.</summary>
    public const string Preload = "Preload";

    /// <summary>The name the first evaluation and preload hold the lock under.</summary>
    public const string Initialization = "Initialization";

    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Guards the waiters and the holder; it is held only to update them, never while the lock is awaited.</summary>
    private readonly Lock _accounting = new();

    private readonly List<Waiter> _waiters = [];
    private string? _holder;
    private long _heldSince;

    /// <summary>Waits for the lock and holds it under <paramref name="holder"/>.</summary>
    /// <param name="holder">What is about to hold the lock, as the next waiter's phase line names it.</param>
    /// <param name="cancellationToken">Stops waiting; the lock is then not held.</param>
    /// <returns>What held the lock longest while this caller waited, or null when nothing held it during the wait.</returns>
    public async Task<string?> EnterAsync(string holder, CancellationToken cancellationToken)
    {
        var waiter = new Waiter(Stopwatch.GetTimestamp());
        lock (_accounting)
            _waiters.Add(waiter);
        try
        {
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_accounting)
                _waiters.Remove(waiter);
            throw;
        }

        lock (_accounting)
        {
            _waiters.Remove(waiter);
            _holder = holder;
            _heldSince = Stopwatch.GetTimestamp();
            return waiter.Held.Count == 0 ? null : waiter.Held.MaxBy(h => h.Value).Key;
        }
    }

    /// <summary>Releases the lock, crediting the time it was held to every caller still waiting.</summary>
    public void Exit()
    {
        lock (_accounting)
        {
            var now = Stopwatch.GetTimestamp();
            if (_holder is not null)
            {
                foreach (var waiter in _waiters)
                {
                    var overlap = now - Math.Max(_heldSince, waiter.Since);
                    if (overlap > 0)
                        waiter.Held[_holder] = waiter.Held.GetValueOrDefault(_holder) + overlap;
                }
            }

            _holder = null;
        }

        _lock.Release();
    }

    /// <summary>Disposes the lock, once the engine has stopped serving requests and the background load has stopped.</summary>
    public void Dispose() => _lock.Dispose();

    /// <summary>
    ///     One caller waiting for the lock: when it started, and how long each holder held the lock since. A class, not a
    ///     record, so two waiters that started on the same tick stay two entries.
    /// </summary>
    private sealed class Waiter(long since)
    {
        public long Since { get; } = since;

        public Dictionary<string, long> Held { get; } = new(StringComparer.Ordinal);
    }
}
