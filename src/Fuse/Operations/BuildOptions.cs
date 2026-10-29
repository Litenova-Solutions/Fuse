namespace Fuse.Operations;

/// <summary>How <c>fuse build</c> shows a build: how many errors, in what form, and whether the warnings are listed.</summary>
/// <param name="MaxShown">How many errors to print, or <see cref="int.MaxValue"/> for every error.</param>
/// <param name="Json">True for one JSON object per diagnostic instead of text.</param>
/// <param name="ListWarnings">True to list the warnings after the errors.</param>
internal sealed record BuildOptions(int MaxShown, bool Json, bool ListWarnings)
{
    /// <summary>How a build shows without flags: the first 20 errors as text, warnings counted only.</summary>
    internal static BuildOptions Default { get; } = new(20, false, false);
}
