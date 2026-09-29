namespace Fuse.Protocol;

/// <summary>How to run one test assembly or project.</summary>
/// <param name="Project">Absolute path of the test project file.</param>
/// <param name="Name">Project name for output.</param>
/// <param name="Mode">A shadow run from the assembly it names, or a build with MSBuild.</param>
/// <param name="Filter">A VSTest filter expression, or null to run every test in the project.</param>
/// <param name="UsesTestingPlatform">True when the project runs on Microsoft.Testing.Platform, which takes different arguments.</param>
internal sealed record TestRun(string Project, string Name, TestRunMode Mode, string? Filter, bool UsesTestingPlatform);
