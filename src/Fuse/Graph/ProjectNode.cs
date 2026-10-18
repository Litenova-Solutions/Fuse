using Fuse.Paths;

namespace Fuse.Graph;

/// <summary>One evaluated C# project: what it compiles, what it references, and whether it holds tests.</summary>
internal sealed class ProjectNode
{
    /// <summary>The project file.</summary>
    public required RepoPath Path { get; init; }

    public required string Name { get; init; }

    /// <summary>The directory that holds the project file.</summary>
    public required RepoPath Directory { get; init; }

    /// <summary>The project files it references, including any outside the repository, for which the graph has no node.</summary>
    public required IReadOnlyList<RepoPath> References { get; init; }

    /// <summary>The C# and Razor sources the project compiles.</summary>
    public required IReadOnlySet<RepoPath> Sources { get; init; }

    /// <summary>The files whose change requires re-evaluating this project (itself and its imports inside the repository).</summary>
    public required IReadOnlySet<RepoPath> EvaluationInputs { get; init; }

    /// <summary>The restore output; missing means the project must be restored before it can compile.</summary>
    public required RepoPath AssetsFile { get; init; }

    public required bool IsTest { get; init; }

    /// <summary>True for an application (console, web, worker) rather than a library: a framework, not source code, calls into it.</summary>
    public required bool IsExecutable { get; init; }

    /// <summary>
    ///     True when the project is a Microsoft.Testing.Platform test application rather than a VSTest project, from
    ///     what its packages set, whether or not the repository's <c>global.json</c> opts <c>dotnet test</c> into the
    ///     platform.
    /// </summary>
    public required bool UsesTestingPlatform { get; init; }

    /// <summary>The test framework its packages name, which spells a platform run's filter.</summary>
    public required TestFramework Framework { get; init; }

    /// <summary>
    ///     The absolute path of what a build of the project writes, one per target framework: the test application a
    ///     platform run without the <c>global.json</c> opt-in runs directly.
    /// </summary>
    public required IReadOnlyList<string> TargetPaths { get; init; }

    /// <summary>
    ///     The absolute path of the runsettings file the project names in <c>RunSettingsFilePath</c>, which
    ///     <c>dotnet test</c> on the project uses; null when it names none.
    /// </summary>
    public string? RunSettings { get; init; }

    public override string ToString() => Name;
}
