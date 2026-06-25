using System.IO.Pipelines;
using StreamJsonRpc;

namespace Fuse.Cli.Rpc;

/// <summary>
///     Builds the configured <see cref="JsonRpc" /> connection for the UI transport, so the wire setup (header
///     framing plus the source-generated JSON formatter) is defined in one place and exercised identically by
///     the <c>fuse host</c> command and the tests.
/// </summary>
public static class FuseHostConnection
{
    /// <summary>
    ///     Attaches a JSON-RPC connection serving <paramref name="service" /> over a duplex pipe, using
    ///     Content-Length header framing (matching the VS Code <c>vscode-jsonrpc</c> client) and the
    ///     source-generated <see cref="FuseHostJsonContext" /> for all DTO serialization.
    /// </summary>
    /// <param name="reader">The inbound half of the transport.</param>
    /// <param name="writer">The outbound half of the transport.</param>
    /// <param name="service">The RPC target whose <c>fuse/*</c> methods are exposed.</param>
    /// <param name="notifier">Optional connection registry for server-to-client notifications (file invalidation).</param>
    /// <returns>A started <see cref="JsonRpc" /> connection; await its <see cref="JsonRpc.Completion" /> to run.</returns>
    public static JsonRpc Attach(PipeReader reader, PipeWriter writer, FuseHostService service, HostNotifier? notifier = null)
    {
        var formatter = new SystemTextJsonFormatter();
        // Reflection-free serialization: route DTOs through the source-generated context (project invariant).
        formatter.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, FuseHostJsonContext.Default);

        var handler = new HeaderDelimitedMessageHandler(writer, reader, formatter);
        var rpc = new JsonRpc(handler);
        rpc.AddLocalRpcTarget(service, new JsonRpcTargetOptions { AllowNonPublicInvocation = false });

        // Register for server-to-client broadcasts (for example fuse/invalidated) and drop the registration when
        // the connection closes, so the host pushes only to live editor windows.
        if (notifier is not null)
        {
            notifier.Register(rpc);
            rpc.Disconnected += (_, _) => notifier.Unregister(rpc);
        }

        rpc.StartListening();
        return rpc;
    }
}
