using System.IO.Pipelines;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using StreamJsonRpc;

namespace Fuse.Cli.Rpc;

/// <summary>
///     A short-lived client to a running <c>fuse host</c> / <c>fuse mcp serve</c> over the UI transport, used by
///     the S3 ambient-verification commands (<c>fuse check --delta</c>, <c>fuse gate</c>) to fetch the
///     <c>fuse/check</c> delta from the resident process. It connects to the deterministic endpoint for a root
///     (<see cref="HostEndpoint" />), handshakes, invokes <c>fuse/check</c>, and disposes the connection.
/// </summary>
/// <remarks>
///     Never throws for the absence of a host: when no process is serving the root (the endpoint is missing or the
///     connect times out) or the host protocol does not match, it returns <c>null</c>, so a hook exits silently and
///     never blocks editing. The connect timeout bounds the no-host probe; a live host on the local machine
///     connects well inside it.
/// </remarks>
public static class FuseHostClient
{
    /// <summary>
    ///     Asks the running host for the check delta of a session, or returns <c>null</c> when no compatible host
    ///     serves the root.
    /// </summary>
    /// <param name="root">The absolute repository root.</param>
    /// <param name="session">The check-session id whose baseline the delta is measured against.</param>
    /// <param name="connectTimeout">How long to wait for a connection before giving up (the no-host probe bound).</param>
    /// <param name="cancellationToken">A token to cancel the call.</param>
    /// <returns>The delta from <c>fuse/check</c>, or <c>null</c> when no compatible host serves the root.</returns>
    public static async Task<CheckDeltaDto?> TryCheckDeltaAsync(
        string root, string session, TimeSpan connectTimeout, CancellationToken cancellationToken)
    {
        Stream? stream = null;
        try
        {
            stream = await ConnectAsync(root, connectTimeout, cancellationToken);
            if (stream is null)
                return null;

            var formatter = new SystemTextJsonFormatter();
            formatter.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, FuseHostJsonContext.Default);
            formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(
                PipeWriter.Create(stream), PipeReader.Create(stream), formatter));
            rpc.StartListening();

            var handshake = await rpc.InvokeWithCancellationAsync<FuseHostHandshake>(
                "fuse/handshake", [], cancellationToken);
            // A protocol mismatch means a stale host; treat it as no host rather than risk a malformed exchange.
            if (handshake.ProtocolVersion != FuseHostService.ProtocolVersion)
                return null;

            return await rpc.InvokeWithCancellationAsync<CheckDeltaDto>(
                "fuse/check", [handshake.SessionToken, root, session], cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No host, a stale endpoint, or a transient RPC error: stay silent so the hook never blocks editing.
            return null;
        }
        finally
        {
            if (stream is not null)
                await stream.DisposeAsync();
        }
    }

    // Connects to the deterministic endpoint for a root: a named pipe on Windows, a Unix domain socket elsewhere.
    // Returns null (not throw) when nothing is serving, so the caller stays silent.
    private static async Task<Stream?> ConnectAsync(string root, TimeSpan connectTimeout, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var client = new NamedPipeClientStream(".", HostEndpoint.PipeName(root), PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            try
            {
                await client.ConnectAsync((int)connectTimeout.TotalMilliseconds, cancellationToken);
                return client;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await client.DisposeAsync();
                return null;
            }
        }

        var socketPath = HostEndpoint.SocketPath(root);
        if (!File.Exists(socketPath))
            return null; // No socket file means no host is bound for this root.

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(connectTimeout);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeoutCts.Token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            socket.Dispose();
            return null;
        }
    }
}
