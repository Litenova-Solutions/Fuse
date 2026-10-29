namespace Fuse.Protocol;

/// <summary>How one reported test ended, as the client reports it and the engine stores it.</summary>
internal enum ReportedVerdict
{
    /// <summary>The test ran and passed.</summary>
    Passed,

    /// <summary>The test ran and failed.</summary>
    Failed,

    /// <summary>The test did not run to a pass or a failure.</summary>
    Skipped,
}
