namespace Fuse.Protocol;

/// <summary>How to run one test assembly or project.</summary>
/// <param name="Project">Absolute path of the test project file.</param>
/// <param name="Name">Project name for output.</param>
/// <param name="Mode">A shadow run from the assembly it names, or a build with MSBuild.</param>
/// <param name="Filter">A VSTest filter expression, or null to run every test in the project.</param>
/// <param name="UsesTestingPlatform">True when the project runs on Microsoft.Testing.Platform, which takes different arguments.</param>
/// <param name="RunSettings">
///     Absolute path of the project's own runsettings file (<c>RunSettingsFilePath</c>), or null. The client writes
///     <paramref name="Filter"/> into a copy of it.
/// </param>
/// <param name="FilterArguments">
///     The filter arguments for a run on Microsoft.Testing.Platform, in its framework's spelling; empty to run every
///     test in the project, and for every VSTest run.
/// </param>
/// <param name="TargetPaths">
///     What a build of the project writes, one per target framework, for a run on Microsoft.Testing.Platform in a
///     repository whose <c>global.json</c> does not opt <c>dotnet test</c> in: the client builds the project and runs
///     the test application directly. Empty for every other run.
/// </param>
internal sealed record TestRun(string Project, string Name, TestRunMode Mode, string? Filter, bool UsesTestingPlatform, string? RunSettings, string[] FilterArguments, string[] TargetPaths)
{
    /// <summary>True when this test slice was satisfied from the content-addressed slice cache without executing.</summary>
    public bool FromCache { get; init; }

    /// <summary>Optional explanation for a cached slice verdict.</summary>
    public string? CacheReason { get; init; }

    /// <summary>
    ///     The content-addressed slice key of this run's test project closure, which the client sends back with the
    ///     run's verdict so the next identical plan reuses it. Null when the run has none, or under
    ///     <c>--no-cache</c>, which neither reads nor writes the cache.
    /// </summary>
    public string? SliceKey { get; init; }

    // A record compares its fields, and an array field by the array itself; two runs with the same arguments in
    // different arrays are the same run, as one read back from the pipe must equal the one that was sent.
    public bool Equals(TestRun? other) =>
        other is not null
        && EqualityContract == other.EqualityContract
        && Project == other.Project
        && Name == other.Name
        && Mode == other.Mode
        && Filter == other.Filter
        && UsesTestingPlatform == other.UsesTestingPlatform
        && RunSettings == other.RunSettings
        && FilterArguments.SequenceEqual(other.FilterArguments)
        && TargetPaths.SequenceEqual(other.TargetPaths);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Project);
        hash.Add(Name);
        hash.Add(Mode);
        hash.Add(Filter);
        hash.Add(UsesTestingPlatform);
        hash.Add(RunSettings);
        foreach (var argument in FilterArguments)
            hash.Add(argument);
        foreach (var target in TargetPaths)
            hash.Add(target);
        return hash.ToHashCode();
    }
}
