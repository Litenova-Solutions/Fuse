using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Fuse.Protocol;

namespace Fuse.Engine;

/// <summary>
///     The engine's live watch events and their subscribers: the router publishes each event once, and every
///     subscription the engine serves reads the events published after it subscribed. A subscription either reads
///     every event or only the events of one session.
/// </summary>
internal sealed class WatchHub
{
    /// <summary>How many events a subscription keeps while its reader is behind; past this the oldest is dropped.</summary>
    internal const int KeptEvents = 64;

    private readonly Lock _lock = new();
    private readonly List<Subscription> _subscriptions = [];

    /// <summary>Hands <paramref name="watchEvent"/> to every matching subscription. Never blocks: a full subscription drops its oldest event.</summary>
    public void Publish(WatchEvent watchEvent)
    {
        lock (_lock)
        {
            foreach (var subscription in _subscriptions)
            {
                if (subscription.Matches(watchEvent))
                    _ = subscription.Channel.Writer.TryWrite(watchEvent);
            }
        }
    }

    /// <summary>
    ///     Reads the events published after this call: every event when <paramref name="all"/> holds, else only the
    ///     events of <paramref name="session"/>. Ends when <paramref name="cancellationToken"/> is cancelled, which
    ///     removes the subscription.
    /// </summary>
    public async IAsyncEnumerable<WatchEvent> SubscribeAsync(string? session, bool all, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<WatchEvent>(new BoundedChannelOptions(KeptEvents)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var subscription = new Subscription(session, all, channel);
        lock (_lock)
            _subscriptions.Add(subscription);
        try
        {
            await foreach (var watchEvent in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return watchEvent;
        }
        finally
        {
            lock (_lock)
                _subscriptions.Remove(subscription);
            channel.Writer.Complete();
        }
    }

    /// <summary>One subscriber: its filter and the bounded channel that drops the oldest event when full.</summary>
    private sealed class Subscription(string? session, bool all, Channel<WatchEvent> channel)
    {
        /// <summary>The events the subscriber has not read yet.</summary>
        public Channel<WatchEvent> Channel { get; } = channel;

        /// <summary>True when this subscriber reads <paramref name="watchEvent"/>: every event when it reads all, else only its session's.</summary>
        public bool Matches(WatchEvent watchEvent) =>
            all || string.Equals(watchEvent.Session, session, StringComparison.Ordinal);
    }
}
