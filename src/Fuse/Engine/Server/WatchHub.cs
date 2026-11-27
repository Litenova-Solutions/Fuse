using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
namespace Fuse.Engine.Server;

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
    ///     Reads the events <paramref name="scope"/> names published after this call. The subscription is registered
    ///     before this returns, so events published before the caller starts enumerating are kept. Ends when
    ///     <paramref name="cancellationToken"/> is cancelled, which removes the subscription.
    /// </summary>
    public IAsyncEnumerable<WatchEvent> SubscribeAsync(WatchScope scope, CancellationToken cancellationToken) => scope switch
    {
        WatchScope.AllSessions => SubscribeAsync(session: null, all: true, cancellationToken),
        WatchScope.OneSession one => SubscribeAsync(one.Session, all: false, cancellationToken),
        _ => throw new UnreachableException($"a watch scope is all sessions or one session, not {scope.GetType().Name}"),
    };

    /// <summary>
    ///     Reads the events published after this call: every event when <paramref name="all"/> holds, else only the
    ///     events of <paramref name="session"/>. The subscription is registered before this returns, so events
    ///     published before the caller starts enumerating are kept. Ends when <paramref name="cancellationToken"/>
    ///     is cancelled, which removes the subscription.
    /// </summary>
    public IAsyncEnumerable<WatchEvent> SubscribeAsync(string? session, bool all, CancellationToken cancellationToken)
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

        // A subscription nobody enumerates would otherwise stay registered after its token is cancelled.
        var registration = cancellationToken.Register(() => Unregister(subscription));
        return ReadAsync(subscription, channel, registration, cancellationToken);
    }

    /// <summary>Reads a subscription's channel until its token is cancelled, then removes the subscription.</summary>
    private async IAsyncEnumerable<WatchEvent> ReadAsync(
        Subscription subscription,
        Channel<WatchEvent> channel,
        CancellationTokenRegistration registration,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var watchEvent in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return watchEvent;
        }
        finally
        {
            registration.Dispose();
            Unregister(subscription);
            _ = channel.Writer.TryComplete();
        }
    }

    /// <summary>Removes a subscription so it receives no more events and can be collected.</summary>
    private void Unregister(Subscription subscription)
    {
        lock (_lock)
            _subscriptions.Remove(subscription);
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
