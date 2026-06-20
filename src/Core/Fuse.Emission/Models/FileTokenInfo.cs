namespace Fuse.Emission.Models;

/// <summary>
///     Pairs a file path with its measured token consumption.
/// </summary>
/// <param name="Path">The file path the token count applies to.</param>
/// <param name="Count">The number of tokens consumed by the file.</param>
public sealed record FileTokenInfo(string Path, long Count);
