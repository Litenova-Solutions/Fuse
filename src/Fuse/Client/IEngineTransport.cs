using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
namespace Fuse.Client;

/// <summary>
///     The port a client sends engine requests through: one request, one answer, with the engine started when none runs
///     and Busy answers retried within the timeout. <see cref="EngineClient.Default"/> talks to the engine over its pipe;
///     a test passes its own transport to a type that takes one.
/// </summary>
internal interface IEngineTransport
{
    /// <summary>Sends <paramref name="request"/> to the engine for <paramref name="root"/> and returns its answer.</summary>
    /// <param name="root">The repository whose engine answers.</param>
    /// <param name="request">The request to send.</param>
    /// <param name="timeout">How long to wait for the answer, retries included.</param>
    /// <param name="onBusy">Told each time the engine answers Busy and the client waits, or null.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    Task<EngineResponse> SendAsync(RepoRoot root, EngineRequest request, TimeSpan timeout, Action<EngineResponse.Busy>? onBusy, CancellationToken cancellationToken);
}
