namespace Fuse.Protocol;

/// <summary>Every way a Fuse operation can fail. The message that carries one explains it, and names the fix where there is one.</summary>
internal enum ErrorCode
{
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
