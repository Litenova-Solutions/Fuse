namespace Fuse.Protocol.Requests;

/// <summary>
///     Registers or removes an immutable contract surface guard (<c>fuse session guard &lt;name&gt; --freeze|--unfreeze</c>),
///     alerting watchers when any declaration inside a frozen path pattern changes.
/// </summary>
/// <param name="Name">The guard or session name.</param>
/// <param name="Unfreeze">True to remove the guard; false to register or update frozen patterns.</param>
/// <param name="Patterns">Repository-relative glob patterns whose declaration surface is frozen.</param>
internal sealed record FreezeGuardRequest(string Name, bool Unfreeze, string[]? Patterns = null)
{
    /// <summary>The guard or session name. A line without it reads as empty.</summary>
    public string Name { get; init; } = Name ?? "";

    /// <summary>Repository-relative glob patterns whose declaration surface is frozen.</summary>
    public string[] Patterns { get; init; } = Patterns ?? [];
}
