namespace Fuse.Graph;

/// <summary>One evaluated C# project: what it compiles, what it references, and whether it holds tests.</summary>
internal sealed class ProjectNode
{
    public required string Path { get; init; }

    public required string Name { get; init; }

    public required string Directory { get; init; }

    /// <summary>Absolute paths of referenced project files.</summary>
    public required IReadOnlyList<string> References { get; init; }

    /// <summary>Absolute paths of the C# and Razor sources the project compiles.</summary>
    public required IReadOnlySet<string> Sources { get; init; }

    /// <summary>Absolute paths of the files whose change requires re-evaluating this project (itself and its imports inside the repository).</summary>
    public required IReadOnlySet<string> EvaluationInputs { get; init; }

    /// <summary>The restore output; missing means the project must be restored before it can compile.</summary>
    public required string AssetsFile { get; init; }

    public required bool IsTest { get; init; }

    /// <summary>True when the tests run on Microsoft.Testing.Platform rather than VSTest.</summary>
    public required bool IsTestingPlatform { get; init; }

    public override string ToString() => Name;
}
