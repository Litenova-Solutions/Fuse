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
///     True when the project runs on Microsoft.Testing.Platform, which takes different arguments. Such a project always
///     builds with MSBuild and runs whole, because its filters differ per test framework.
/// </param>
internal sealed record PlannedRun(RepoPath Project, string Name, RunMode Mode, string? Filter, bool UsesTestingPlatform);
