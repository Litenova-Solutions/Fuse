using Xunit;

namespace Fuse.Cli.Tests.Host;

/// <summary>
///     Guards integration tests that need the .NET SDK (<c>dotnet build</c>) and, for some paths, the bundled
///     build-capture worker. Default CI excludes them:
///     <c>dotnet test Fuse.slnx -c Release --no-build --filter "Category!=RequiresSdk"</c>
///     Release publish smoke sets <c>FUSE_PUBLISH_SMOKE=1</c> or runs with the worker bundled beside
///     <c>fuse.dll</c>; in that layout a silent abstain is a failure.
/// </summary>
public static class RequiresSdkIntegration
{
    /// <summary>The xUnit trait name used to mark SDK-dependent integration tests.</summary>
    public const string TraitName = "Category";

    /// <summary>The xUnit trait value CI filters out of the default test run.</summary>
    public const string TraitValue = "RequiresSdk";

    /// <summary>
    ///     Whether the current run is the Release publish smoke path that must not silently abstain.
    /// </summary>
    public static bool PublishSmoke =>
        string.Equals(Environment.GetEnvironmentVariable("FUSE_PUBLISH_SMOKE"), "1", StringComparison.Ordinal)
        || string.Equals(Environment.GetEnvironmentVariable("FUSE_PUBLISH_SMOKE"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Whether the build-capture worker ships beside the given fuse assembly (published or dev output layout).
    /// </summary>
    /// <param name="fuseDllPath">The absolute path to <c>fuse.dll</c>.</param>
    public static bool WorkerBundled(string fuseDllPath)
    {
        var dir = Path.GetDirectoryName(fuseDllPath);
        return dir is not null
            && File.Exists(Path.Combine(dir, "build-capture", "fuse-build-capture.dll"));
    }

    /// <summary>
    ///     Whether a Release or Debug build already copied the worker beside <c>fuse.dll</c> under the repo.
    /// </summary>
    /// <param name="repoRoot">The repository root containing <c>Fuse.slnx</c>.</param>
    public static bool WorkerBundledInRepo(string? repoRoot)
    {
        if (repoRoot is null)
            return false;

        foreach (var config in new[] { "Release", "Debug" })
        {
            var fuseDll = Path.Combine(repoRoot, "src", "Host", "Fuse.Cli", "bin", config, "net10.0", "fuse.dll");
            if (File.Exists(fuseDll) && WorkerBundled(fuseDll))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Skips when an expected artifact is absent, or fails when publish smoke or a bundled worker requires it.
    /// </summary>
    /// <param name="path">The artifact path, or null when not resolved.</param>
    /// <param name="description">A short label for failure and skip messages.</param>
    public static void RequireArtifact(string? path, string description)
    {
        if (path is not null && File.Exists(path))
            return;

        if (PublishSmoke || (path is not null && WorkerBundled(path)))
            throw new Xunit.Sdk.XunitException($"{description} is required but missing at '{path}'.");

        throw Xunit.Sdk.SkipException.ForSkip($"{description} not built; skipped (RequiresSdk).");
    }

    /// <summary>
    ///     Skips when a precondition is unmet, or fails when publish smoke requires the test to pass.
    /// </summary>
    /// <param name="condition">When true, the guard passes.</param>
    /// <param name="abstainReason">The reason recorded on skip or failure.</param>
    public static void RequireCondition(bool condition, string abstainReason)
    {
        if (condition)
            return;

        if (PublishSmoke)
            throw new Xunit.Sdk.XunitException($"Publish smoke required this test to pass: {abstainReason}");

        throw Xunit.Sdk.SkipException.ForSkip(abstainReason);
    }
}
