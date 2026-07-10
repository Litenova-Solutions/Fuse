namespace Fuse.Fusion;

/// <summary>
///     Describes the active scoping mode on a fusion request for explain and preview surfaces.
/// </summary>
public static class FusionScopeDescriptor
{
    /// <summary>
    ///     Returns a short human-readable description of the active scope on <paramref name="request" />.
    /// </summary>
    /// <param name="request">The fusion request whose focus or change options are described.</param>
    /// <returns>A scope summary string suitable for explain output headers.</returns>
    public static string Describe(FusionRequest request)
    {
        if (request.Focus is not null)
            return $"focus '{request.Focus.Seed}' depth {request.Focus.Depth}";
        if (request.Changes is not null)
            return $"changed since '{request.Changes.Since}'";
        return "all collected files";
    }
}
