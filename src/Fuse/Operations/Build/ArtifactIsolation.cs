namespace Fuse.Operations.Build;

/// <summary>Where a build lease writes its MSBuild output: the repository folders it always used, or an isolated slot under the state directory.</summary>
internal abstract record ArtifactIsolation
{
    private ArtifactIsolation()
    {
    }

    /// <summary>Build output stays in the repository folders it always used.</summary>
    internal sealed record None : ArtifactIsolation;

    /// <summary>Output goes to a unique slot this acquisition created, deleted when the lock is released and no build holds it.</summary>
    /// <param name="Path">The slot folder under the state directory.</param>
    internal sealed record Owned(string Path) : ArtifactIsolation;

    /// <summary>Output goes to a slot shared by worker key, never deleted, because another build may still use it after this lock goes.</summary>
    /// <param name="Path">The slot folder under the state directory.</param>
    internal sealed record Shared(string Path) : ArtifactIsolation;
}
