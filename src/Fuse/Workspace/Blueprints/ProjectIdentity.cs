using Fuse.Graph;
namespace Fuse.Workspace.Blueprints;

/// <summary>
///     Which evaluated project a blueprint entry describes. The path and name tell multi-targeted variants
///     apart, and the framework tells whether the project runs tests.
/// </summary>
internal sealed record ProjectIdentity(
    string ProjectPath,
    string Name,
    string AssemblyName,
    string? TargetFramework,
    bool IsTestProject,
    TestFramework TestFramework);
