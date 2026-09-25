namespace Fuse.Protocol;

/// <summary>One request from a client to the engine. Each pipe connection carries exactly one.</summary>
/// <param name="Version">The client's exact build id. A mismatch makes the engine answer <see cref="ResponseStatus.Restart"/> and exit.</param>
/// <param name="Kind">What the client asks for.</param>
/// <param name="Files">For <see cref="RequestKind.Check"/>: repository files to scope the check to; null checks every change since HEAD.</param>
/// <param name="Wait">True to wait for the engine to finish loading; false to get <see cref="ErrorCode.Loading"/> immediately.</param>
/// <param name="AllTests">For <see cref="RequestKind.TestPlan"/>: plan every test instead of the affected ones.</param>
internal sealed record EngineRequest(
    string Version,
    RequestKind Kind,
    string[]? Files = null,
    bool Wait = true,
    bool AllTests = false);

/// <summary>The operations the engine serves.</summary>
internal enum RequestKind
{
    /// <summary>Liveness probe; answers once the pipe is up, even while loading.</summary>
    Ping,

    /// <summary>Errors the working tree has that HEAD did not.</summary>
    Check,

    /// <summary>Selects the affected tests and prepares the fastest safe way to run them.</summary>
    TestPlan,

    /// <summary>Stops the engine.</summary>
    Shutdown,
}
