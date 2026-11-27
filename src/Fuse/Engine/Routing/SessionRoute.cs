using System.Diagnostics;
using Fuse.Check.Sessions;
using Fuse.Engine.Server;
using Fuse.Failures;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
using Fuse.Telemetry;
namespace Fuse.Engine.Routing;

/// <summary>
///     Answers the session requests without the scheduler, at once: a registration or a guard goes through its own
///     lock, and a watch subscription reads the hub's first event. None waits for initialization.
/// </summary>
internal sealed class SessionRoute
{
    private readonly SessionEdits _sessions;
    private readonly SurfaceGuardStore _guards;
    private readonly WatchHub? _watchHub;
    private readonly RepoRoot _root;
    private readonly EngineLog _log;

    internal SessionRoute(SessionEdits sessions, SurfaceGuardStore guards, WatchHub? watchHub, RepoRoot root, EngineLog log)
    {
        _sessions = sessions;
        _guards = guards;
        _watchHub = watchHub;
        _root = root;
        _log = log;
    }

    /// <summary>
    ///     Answers a session registration or a session guard without the scheduler, at once: <see cref="SessionEdits"/>
    ///     applies a registration under its own lock, so a check that reads its writers under that lock sees the
    ///     registration whole or not at all. It waits for no initialization and holds no reader; neither needs the workspace.
    ///     A check answered after a registration counts its files as that session's from that answer on.
    /// </summary>
    internal Task<EngineResponse> SessionAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(request switch
        {
            EngineRequest.BeginSession begin => BeginSession(begin),
            EngineRequest.EndSession end => EndSession(end),
            EngineRequest.SessionGuard guard => GuardSession(guard),
            _ => throw new UnreachableException($"{request.GetType().Name} is not a session request"),
        });
    }

    /// <summary>
    ///     Answers a watch subscription with its first event. The engine server streams the rest without calling here:
    ///     it subscribes to the watch hub itself and writes one line per event until the client disconnects.
    /// </summary>
    internal async Task<EngineResponse> WatchFirstAsync(EngineRequest.WatchStream watch, CancellationToken cancellationToken)
    {
        if (_watchHub is null)
            return new EngineResponse.WatchEventPushed(new WatchEvent("heartbeat", watch.Session, _root.Path, Message: "watching"));
        await foreach (var watchEvent in _watchHub.SubscribeAsync(watch.Session, watch.All, cancellationToken).ConfigureAwait(false))
            return new EngineResponse.WatchEventPushed(watchEvent);
        throw new UnreachableException("a watch subscription ends only when cancelled");
    }

    /// <summary>Registers the session that <paramref name="begin"/> names.</summary>
    private EngineResponse BeginSession(EngineRequest.BeginSession begin)
    {
        try
        {
            _sessions.BeginRegistration(begin.Name, begin.Paths, begin.Pid);
            return new EngineResponse.Acknowledged();
        }
        catch (FuseException e)
        {
            return ResponseMapper.Unanswered(e);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Write($"BeginSession failed: {e}");
            return new EngineResponse.Unanswered(ErrorCode.Internal, $"internal error: {e.Message} (details in {_log.FilePath})");
        }
    }

    /// <summary>Ends the registration that <paramref name="end"/> names.</summary>
    private EngineResponse EndSession(EngineRequest.EndSession end)
    {
        try
        {
            return _sessions.EndRegistration(end.Name)
                ? new EngineResponse.Acknowledged()
                : new EngineResponse.Unanswered(ErrorCode.NoRegisteredSession, ErrorMessages.NoRegisteredSession(end.Name));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Write($"EndSession failed: {e}");
            return new EngineResponse.Unanswered(ErrorCode.Internal, $"internal error: {e.Message} (details in {_log.FilePath})");
        }
    }

    /// <summary>Applies the frozen patterns that <paramref name="guard"/> registers or removes.</summary>
    private EngineResponse GuardSession(EngineRequest.SessionGuard guard)
    {
        try
        {
            _guards.Apply(guard.Guard);
            return new EngineResponse.Acknowledged();
        }
        catch (FuseException e)
        {
            return ResponseMapper.Unanswered(e);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Write($"SessionGuard failed: {e}");
            return new EngineResponse.Unanswered(ErrorCode.Internal, $"internal error: {e.Message} (details in {_log.FilePath})");
        }
    }
}
