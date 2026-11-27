namespace Fuse.Inspection;

/// <summary>What a changed build builds for the files changed since HEAD, and the tree it was planned from.</summary>
/// <param name="BuildProjects">Absolute paths of the project files to build, in name order; empty to build nothing or, with <paramref name="FallbackReason"/>, the whole solution.</param>
/// <param name="ChangedInProjects">Names of the projects that own a changed file, in ordinal order.</param>
/// <param name="ChangedFileCount">How many sources differ from HEAD.</param>
/// <param name="TreeHash">The tree stamp the plan was planned from: 12 lowercase hex characters.</param>
/// <param name="FallbackReason">Why to build the whole solution instead, or empty to build <paramref name="BuildProjects"/>.</param>
/// <param name="UnownedFileCount">How many changed files no project compiles.</param>
internal sealed record ChangedBuild(string[] BuildProjects, string[] ChangedInProjects, int ChangedFileCount, string TreeHash, string FallbackReason = "", int UnownedFileCount = 0)
{
    /// <summary>The projects to build.</summary>
    public string[] BuildProjects { get; init; } = BuildProjects ?? [];

    /// <summary>The projects owning changes.</summary>
    public string[] ChangedInProjects { get; init; } = ChangedInProjects ?? [];

    /// <summary>The stamp.</summary>
    public string TreeHash { get; init; } = TreeHash ?? "";

    /// <summary>The fallback reason.</summary>
    public string FallbackReason { get; init; } = FallbackReason ?? "";
}
