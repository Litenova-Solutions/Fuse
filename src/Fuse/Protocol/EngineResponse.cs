namespace Fuse.Protocol;

/// <summary>The engine's answer to one <see cref="EngineRequest"/>.</summary>
/// <param name="Status">Whether the request succeeded.</param>
/// <param name="Error">The failure category when <paramref name="Status"/> is <see cref="ResponseStatus.Error"/>.</param>
/// <param name="Message">A one-line explanation that names the command to run next.</param>
/// <param name="Check">The result of a <see cref="RequestKind.Check"/>.</param>
/// <param name="Tests">The result of a <see cref="RequestKind.TestPlan"/>.</param>
internal sealed record EngineResponse(
    ResponseStatus Status,
    ErrorCode? Error = null,
    string? Message = null,
    CheckReport? Check = null,
    TestPlan? Tests = null)
{
    public static EngineResponse Ok() => new(ResponseStatus.Ok);

    public static EngineResponse Fail(ErrorCode error, string message) => new(ResponseStatus.Error, error, message);
}

/// <summary>Outcome of a request.</summary>
internal enum ResponseStatus
{
    /// <summary>The request succeeded.</summary>
    Ok,

    /// <summary>The request failed; see <see cref="EngineResponse.Error"/>.</summary>
    Error,

    /// <summary>The engine runs a different version and is exiting; the client starts a fresh engine and retries.</summary>
    Restart,
}
