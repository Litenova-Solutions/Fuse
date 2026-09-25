namespace Fuse.Protocol;

/// <summary>Which tests to run and how. The client executes it, so a long test run never blocks the engine.</summary>
/// <param name="Runs">One entry per test run (one per target framework on the fast path, one per project otherwise); empty when no test is affected.</param>
/// <param name="SelectedTests">Test methods selected, counted statically.</param>
/// <param name="TotalTests">Test methods in the repository, counted statically.</param>
/// <param name="Scope">One sentence stating the selection and its reason.</param>
internal sealed record TestPlan(TestRun[] Runs, int SelectedTests, int TotalTests, string Scope);

/// <summary>How to run one test assembly or project.</summary>
/// <param name="Project">Absolute path of the test project file.</param>
/// <param name="Name">Project name for output.</param>
/// <param name="ShadowAssembly">Absolute path of the test assembly in a shadow output with freshly emitted changes, or null to build with MSBuild.</param>
/// <param name="Filter">A VSTest filter expression, or null to run every test in the project.</param>
/// <param name="TestingPlatform">True when the project runs on Microsoft.Testing.Platform, which takes different arguments.</param>
internal sealed record TestRun(string Project, string Name, string? ShadowAssembly, string? Filter, bool TestingPlatform);
