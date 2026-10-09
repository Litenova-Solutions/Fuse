namespace Fuse.Check;

/// <summary>
///     How one check runs analyzers on the working tree. The baseline always runs every analyzer to the end: an error is
///     introduced only when HEAD does not have it, so HEAD's errors must all be known, while a working-tree error that an
///     analyzer did not get to is only one the check does not report.
/// </summary>
/// <param name="CompilationWide">Whether the compilation-wide analyzers (<see cref="AnalyzerPlan"/>) run.</param>
/// <param name="Deadline">Cancelled when the analyzers must stop so the check can still answer in time.</param>
internal sealed record AnalyzerRun(bool CompilationWide, CancellationToken Deadline)
{
    /// <summary>Every analyzer, with no deadline.</summary>
    public static readonly AnalyzerRun Full = new(true, CancellationToken.None);

    /// <summary>
    ///     The share of the client's wait the analyzers may use; the rest is left for what follows them and for the
    ///     answer to reach the client.
    /// </summary>
    public const double ShareOfWait = 0.8;
}
