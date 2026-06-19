namespace Fuse.Languages.Abstractions.Patterns;

/// <summary>
///     Base class for pattern detectors supporting single-pass batch accumulation.
/// </summary>
public abstract class PatternDetectorBase : IPatternDetector
{
    /// <inheritdoc />
    public abstract string PatternName { get; }

    /// <inheritdoc />
    public DetectedPattern? Detect(IReadOnlyList<FusedFileSnapshot> fusedFiles)
    {
        ResetAccumulation();
        foreach (var file in fusedFiles)
            AccumulateFile(file);
        return CompleteAccumulation();
    }

    /// <summary>
    ///     Resets per-run accumulation state before batch processing.
    /// </summary>
    public void ResetAccumulation() => ResetInternal();

    /// <summary>
    ///     Accumulates pattern signals from a single fused file snapshot.
    /// </summary>
    public void AccumulateFile(FusedFileSnapshot file) => AnalyzeFile(file);

    /// <summary>
    ///     Builds the final detected pattern from accumulated state.
    /// </summary>
    public DetectedPattern? CompleteAccumulation() => BuildResult();

    /// <summary>
    ///     Resets internal accumulation state.
    /// </summary>
    protected abstract void ResetInternal();

    /// <summary>
    ///     Analyzes a single file snapshot and updates accumulation state.
    /// </summary>
    protected abstract void AnalyzeFile(FusedFileSnapshot file);

    /// <summary>
    ///     Builds the detected pattern from accumulated state, or returns null when not found.
    /// </summary>
    protected abstract DetectedPattern? BuildResult();
}
