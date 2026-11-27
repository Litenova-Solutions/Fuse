namespace Fuse.Testing.Model;

/// <summary>
///     How a run reaches its test host: the VSTest filter of a plain run, or the platform arguments and built
///     applications of a run on Microsoft.Testing.Platform. A run is one or the other, never a mix.
/// </summary>
internal abstract record TestRunnerPlan
{
    private TestRunnerPlan()
    {
    }

    /// <summary>A run on VSTest, filtered by its VSTest expression and read with its runsettings file.</summary>
    /// <param name="RunSettings">
    ///     The absolute path of the project's own runsettings file, or null when the project names none.
    /// </param>
    public sealed record VsTest(string? RunSettings) : TestRunnerPlan;

    /// <summary>A run on Microsoft.Testing.Platform, with its framework's arguments and built applications.</summary>
    /// <param name="RunSettings">
    ///     The absolute path of the project's own runsettings file, or null when its framework does not read one.
    /// </param>
    /// <param name="FilterArguments">
    ///     The filter arguments in the framework's spelling; empty to run every test in the project.
    /// </param>
    /// <param name="TargetPaths">
    ///     What a build of the project writes, one per target framework, for a repository whose
    ///     <c>global.json</c> does not opt <c>dotnet test</c> in; one shadow assembly for a shadow run.
    /// </param>
    public sealed record TestingPlatform(string? RunSettings, string[] FilterArguments, string[] TargetPaths) : TestRunnerPlan;
}
