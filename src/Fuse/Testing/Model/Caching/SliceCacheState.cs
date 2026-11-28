namespace Fuse.Testing.Model.Caching;

/// <summary>
///     What the slice cache holds for one planned run: nothing computed, a key with no stored verdict, or a key
///     with the stored verdict a later plan reuses. A run is in exactly one of these states.
/// </summary>
internal abstract record SliceCacheState
{
    private SliceCacheState()
    {
    }

    /// <summary>No slice key was computed, or the plan bypassed the cache, so the run executes.</summary>
    public sealed record Uncached : SliceCacheState;

    /// <summary>A slice key with no stored verdict, so the run executes and the client stores its verdict.</summary>
    /// <param name="Key">The content-addressed slice key of the test project closure.</param>
    public sealed record Pending(string Key) : SliceCacheState;

    /// <summary>A slice key with its stored verdict, which the client reuses without executing on a pass.</summary>
    /// <param name="Key">The content-addressed slice key of the test project closure.</param>
    /// <param name="Result">The stored verdict for <paramref name="Key"/>.</param>
    public sealed record Cached(string Key, SliceCacheResult Result) : SliceCacheState;
}
