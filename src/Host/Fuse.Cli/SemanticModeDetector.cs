namespace Fuse.Cli;

/// <summary>
///     Decides at process start whether the opt-in Roslyn precision tier should be registered, before the
///     command line is parsed.
/// </summary>
/// <remarks>
///     Registration of capabilities happens when the dependency-injection container is built, which is before
///     command parsing, so the decision is made by scanning the raw arguments for <c>--semantic</c> and by
///     reading the <c>FUSE_SEMANTIC</c> environment variable. In the Native AOT build the Roslyn assembly is
///     not referenced, so this detector has no registration to enable and the regex tier is always used.
/// </remarks>
internal static class SemanticModeDetector
{
    /// <summary>
    ///     Returns whether semantic analysis was requested via the <c>--semantic</c> argument or the
    ///     <c>FUSE_SEMANTIC</c> environment variable.
    /// </summary>
    /// <param name="args">The raw process arguments.</param>
    /// <returns><see langword="true" /> when semantic analysis is requested.</returns>
    public static bool IsRequested(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is "--semantic" or "/semantic")
                return true;
        }

        return EnvironmentRequested();
    }

    /// <summary>
    ///     Returns whether the <c>FUSE_SEMANTIC</c> environment variable enables semantic analysis.
    /// </summary>
    /// <returns><see langword="true" /> when the variable is set to a value other than <c>0</c> or <c>false</c>.</returns>
    public static bool EnvironmentRequested()
    {
        var value = Environment.GetEnvironmentVariable("FUSE_SEMANTIC");
        return !string.IsNullOrEmpty(value)
            && value != "0"
            && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
    }
}
