namespace Fuse.Cli;

/// <summary>
///     Reads the explicit <c>--embeddings</c> argument before the command line is parsed, so the embedding
///     backend can be chosen when the dependency-injection container is built.
/// </summary>
/// <remarks>
///     This runs before command parsing and scans the raw arguments for <c>--embeddings</c>. It reports only
///     the explicit flag; the environment variable and the build default are folded in by the ONNX registration.
/// </remarks>
internal static class EmbeddingsModeDetector
{
    /// <summary>
    ///     Returns the explicit embedding choice from the arguments, or <see langword="null" /> when no
    ///     <c>--embeddings</c> flag is present.
    /// </summary>
    /// <param name="args">The raw process arguments.</param>
    /// <returns>
    ///     <see langword="true" /> for <c>--embeddings</c> (optionally <c>true</c>/<c>1</c>),
    ///     <see langword="false" /> for <c>--embeddings false</c>/<c>0</c> or <c>--embeddings=false</c>,
    ///     or <see langword="null" /> when the flag is absent.
    /// </returns>
    public static bool? ExplicitFlag(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--embeddings=", StringComparison.Ordinal))
                return ParseValue(arg["--embeddings=".Length..]);

            if (arg is "--embeddings" or "/embeddings")
            {
                // A following token that is a boolean literal is the flag's value; otherwise the flag alone
                // means "on".
                if (i + 1 < args.Count && TryParseBool(args[i + 1], out var value))
                    return value;

                return true;
            }
        }

        return null;
    }

    private static bool ParseValue(string raw) => !TryParseBool(raw, out var value) || value;

    private static bool TryParseBool(string raw, out bool value)
    {
        switch (raw)
        {
            case "1":
            case "true" or "True" or "TRUE":
                value = true;
                return true;
            case "0":
            case "false" or "False" or "FALSE":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }
}
