namespace Fuse.Failures;

/// <summary>
///     A failure that ends a request, with a user-facing message that names the fix. It is thrown where the failure is
///     found, often deep inside loading, and caught where the request is answered, which turns it into an error response.
/// </summary>
internal sealed class FuseException(ErrorCode code, string message) : Exception(message)
{
    /// <summary>What kind of failure this is, which a client reads to decide whether the agent has to act on it.</summary>
    public ErrorCode Code { get; } = code;
}
