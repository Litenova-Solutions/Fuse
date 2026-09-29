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

    /// <summary>True when the tests run on Microsoft.Testing.Platform rather than VSTest.</summary>
    public required bool IsTestingPlatform { get; init; }

    public override string ToString() => Name;
}
