using Fuse.Testing.Model;
using Fuse.Testing.Model.Caching;
namespace Fuse.Tests.Fixtures;

/// <summary>
///     Reads of a <see cref="PlannedRun"/> that tests assert on: whether it runs on Microsoft.Testing.Platform, its
///     runsettings file, its platform filter arguments and its slice key, each taken from the run's runner and cache
///     cases.
/// </summary>
internal static class PlannedRunReads
{
    extension(PlannedRun run)
    {
        /// <summary>True when the run is a Microsoft.Testing.Platform run.</summary>
        public bool UsesTestingPlatform => run.Runner is TestRunnerPlan.TestingPlatform;

        /// <summary>The runsettings file the run passes, or null.</summary>
        public string? RunSettings => run.Runner switch
        {
            TestRunnerPlan.VsTest vsTest => vsTest.RunSettings,
            TestRunnerPlan.TestingPlatform platform => platform.RunSettings,
            _ => null,
        };

        /// <summary>The platform filter arguments of a Microsoft.Testing.Platform run, or none for a VSTest run.</summary>
        public string[] FilterArguments => run.Runner is TestRunnerPlan.TestingPlatform platform ? platform.FilterArguments : [];

        /// <summary>The slice key the run is cached under, or null when it has none.</summary>
        public string? SliceKey => run.Cache switch
        {
            SliceCacheState.Pending pending => pending.Key,
            SliceCacheState.Cached cached => cached.Key,
            _ => null,
        };
    }
}
