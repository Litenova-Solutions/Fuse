namespace Fuse.Protocol;

/// <summary>What happened to the declaration a <see cref="ReportedCause"/> names, which is the word printed before it.</summary>
internal enum CauseKind
{
    /// <summary>The working tree has the declaration, changed or added.</summary>
    Changed,

    /// <summary>The working tree no longer has the declaration.</summary>
    Removed,
}
