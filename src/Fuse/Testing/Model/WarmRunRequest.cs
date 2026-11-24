namespace Fuse.Testing.Model;

/// <summary>Parameters for executing a test slice in <c>WarmTestPool</c>.</summary>
/// <param name="ProjectPath">Absolute path of the test project file.</param>
/// <param name="ProjectName">Display name of the test project.</param>
/// <param name="TargetFramework">Target framework identifier (for example <c>net10.0</c>).</param>
/// <param name="BinDirectory">Baseline build output directory holding NuGet and framework dependencies.</param>
/// <param name="WorkingDirectory">Working directory for executing tests (typically the shadow directory).</param>
/// <param name="TestAssemblyPath">Path to the test assembly on disk for dependency resolution.</param>
/// <param name="TestAssemblySimpleName">Simple assembly name of the test project.</param>
/// <param name="Assemblies">Mutable repository assemblies (test project and repo dependencies) to load into the collectible ALC.</param>
/// <param name="SelectedPatterns">Selected test method names or class prefixes; empty runs every test in the assembly.</param>
internal sealed record WarmRunRequest(
    string ProjectPath,
    string ProjectName,
    string TargetFramework,
    string BinDirectory,
    string WorkingDirectory,
    string TestAssemblyPath,
    string TestAssemblySimpleName,
    IReadOnlyList<WarmAssemblyImage> Assemblies,
    IReadOnlyList<string> SelectedPatterns)
{
    /// <summary>Maximum failures before stopping early.</summary>
    public int MaxFailures { get; init; } = 10;

    /// <summary>Maximum wall time allowed before timing out and recycling the warm worker.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Optional content-addressed slice cache key.</summary>
    public string? SliceKey { get; init; }

    /// <summary>True to bypass <c>SliceTestCache</c> lookup (<c>--clean</c> / <c>--no-cache</c>).</summary>
    public bool NoCache { get; init; }

    /// <summary>True to collect per-test method/type coverage hits during execution.</summary>
    public bool CollectCoverage { get; init; } = true;

    /// <summary>True to keep the collectible ALC alive after the run so subsequent body edits can use MetadataUpdater EnC.</summary>
    public bool KeepAliveForEnc { get; init; } = true;
}
