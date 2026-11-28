namespace Fuse.Operations;

/// <summary>How long an operation took, as a summary names it, the way the build and test summaries do.</summary>
internal static class Duration
{
    /// <summary>
    ///     The duration as a summary prints it (<c>in 0.1 s</c>), or empty when <paramref name="seconds"/> is null,
    ///     so an answer that carries none prints nothing new.
    /// </summary>
    internal static string In(double? seconds) =>
        seconds is null ? "" : $" in {seconds:0.0} s";
}
