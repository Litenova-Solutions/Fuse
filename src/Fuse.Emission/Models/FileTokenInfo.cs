namespace Fuse.Emission.Models;

/// <summary>
///     Represents information about a file's token consumption.
/// </summary>
public sealed record FileTokenInfo(string Path, long Count);
