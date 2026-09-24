using Fuse.Protocol;

namespace Fuse.Workspace;

/// <summary>A failure with a user-facing message that names the fix. The engine turns it into an error response.</summary>
internal sealed class FuseException(ErrorCode code, string message) : Exception(message)
{
    public ErrorCode Code { get; } = code;
}
