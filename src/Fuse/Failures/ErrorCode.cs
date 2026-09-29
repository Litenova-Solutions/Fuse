namespace Fuse.Failures;

/// <summary>
///     Every way a Fuse operation can fail. The message that carries one explains it, and names the fix where there is
///     one. The wire carries a code by its name, so the order of the members means nothing.
/// </summary>
internal enum ErrorCode
{
    /// <summary>The working directory is not inside a git repository, so there is no HEAD to compare with.</summary>
    NotARepository,

    /// <summary>The repository contains no C# project.</summary>
    NoProjects,

    /// <summary>The engine is still evaluating projects or loading compilations.</summary>
    Loading,

    /// <summary>A project has not been restored, so it cannot be compiled.</summary>
    RestoreNeeded,

    /// <summary>A project could not be evaluated or loaded.</summary>
    LoadFailed,

    /// <summary>The engine did not answer in time.</summary>
    Timeout,

    /// <summary>An unexpected failure inside Fuse.</summary>
    Internal,
}
