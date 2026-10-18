using Fuse.Paths;

namespace Fuse.Testing.Model;

/// <summary>
///     One <c>dotnet test</c> process the client starts: one per target framework for a shadow run, one per project for
///     a build with MSBuild.
/// </summary>
/// <param name="Project">The test project file.</param>
/// <param name="Name">The project name, for output.</param>
/// <param name="Mode">A shadow run from the assembly it names, or a build with MSBuild.</param>
/// <param name="Filter">A VSTest filter expression, or null to run every test in the project.</param>
/// <param name="UsesTestingPlatform">
///     True when the project runs on Microsoft.Testing.Platform, which takes different arguments. Such a project
///     always builds with MSBuild; its filter is its framework's <paramref name="FilterArguments"/>, or the VSTest
///     <paramref name="Filter"/> for the frameworks that accept it there.
/// </param>
/// <param name="RunSettings">
///     The absolute path of the project's own runsettings file (<c>RunSettingsFilePath</c>), which the client merges
///     with <paramref name="Filter"/>; null when the project names none, or runs on Microsoft.Testing.Platform under a
///     framework that does not read one.
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
internal sealed record PlannedRun(RepoPath Project, string Name, RunMode Mode, string? Filter, bool UsesTestingPlatform, string? RunSettings, string[] FilterArguments, string[] TargetPaths);
