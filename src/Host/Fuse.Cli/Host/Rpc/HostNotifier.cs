using System.Collections.Concurrent;
using StreamJsonRpc;

namespace Fuse.Cli.Rpc;

/// <summary>
///     Tracks the live JSON-RPC connections for one host so the host can push server-to-client notifications
///     (for example <c>fuse/invalidated</c> when the file watcher fires) to every connected editor window. A
///     connection registers on attach and unregisters when it completes.
/// </summary>
public sealed class HostNotifier
{
    // A connection set keyed by the JsonRpc instance; the bool value is unused (ConcurrentDictionary is the
    // simplest thread-safe set). Broadcasts iterate a snapshot so a connection closing mid-broadcast is safe.
    private readonly ConcurrentDictionary<JsonRpc, byte> _connections = new();

    /// <summary>Registers a connection to receive broadcasts.</summary>
    /// <param name="connection">The JSON-RPC connection.</param>
    public void Register(JsonRpc connection) => _connections.TryAdd(connection, 0);

    /// <summary>Removes a connection (on disconnect) so broadcasts no longer target it.</summary>
    /// <param name="connection">The JSON-RPC connection.</param>
    public void Unregister(JsonRpc connection) => _connections.TryRemove(connection, out _);

    /// <summary>The number of currently registered connections.</summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>
    ///     Sends a parameterless notification to every registered connection. A connection that throws (because it
    ///     is closing) is skipped, so one dead client cannot break the broadcast to the others.
    /// </summary>
    /// <param name="method">The notification method name, for example <c>fuse/invalidated</c>.</param>
    /// <returns>A task that completes when the notification has been dispatched to every live connection.</returns>
    public async Task BroadcastAsync(string method)
    {
        foreach (var connection in _connections.Keys)
        {
            try
            {
                await connection.NotifyAsync(method);
            }
            catch (Exception)
            {
                // The connection is closing or already gone; drop it and continue with the rest.
                _connections.TryRemove(connection, out _);
            }
        }
    }
}
