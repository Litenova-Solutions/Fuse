namespace Fuse.Dotnet;

/// <summary>How one test ended in the last run, as the results record keeps it.</summary>
internal enum TestVerdict
{
    /// <summary>The test ran and passed.</summary>
    Passed,

    /// <summary>The test ran and failed.</summary>
    Failed,

    /// <summary>The test did not run to a pass or a failure.</summary>
    Skipped,
}
