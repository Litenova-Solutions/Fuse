namespace Fuse.Testing.Model.Caching;

/// <summary>Cached test outcomes for one test project slice, keyed by deterministic content hashes.</summary>
/// <param name="SliceKey">The SHA-256 content key of the test project closure, non-sources, runsettings and filter.</param>
/// <param name="ProjectPath">Repository-relative or canonical path of the test project.</param>
/// <param name="ProjectName">Display name of the test project.</param>
/// <param name="TargetFramework">Target framework name, or empty when single-targeted.</param>
/// <param name="Passed">Number of passing tests.</param>
/// <param name="Failed">Number of failing tests.</param>
/// <param name="Skipped">Number of skipped tests.</param>
/// <param name="DurationMs">Measured duration of the original execution in milliseconds.</param>
/// <param name="CachedAtUtc">Timestamp when this slice entry was written.</param>
/// <param name="Failures">Recorded failure details when <paramref name="Failed"/> is greater than zero.</param>
internal sealed record SliceCacheResult(
    string SliceKey,
    string ProjectPath,
    string ProjectName,
    string TargetFramework,
    int Passed,
    int Failed,
    int Skipped,
    double DurationMs,
    DateTime CachedAtUtc,
    IReadOnlyList<SliceTestFailure> Failures);
