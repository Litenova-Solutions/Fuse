namespace Fuse.Plugins.Abstractions.Patterns;

/// <summary>
///     Base class for <see cref="IPatternDetector" /> implementations that detect a pattern by accumulating
///     signals across files in a single pass.
/// </summary>
/// <remarks>
///     The accumulation lifecycle runs in three phases per detection: reset
///     (<see cref="ResetAccumulation" />), one analyze call per file (<see cref="AccumulateFile" />), then a
///     finalize call (<see cref="CompleteAccumulation" />). The public phase methods are also exposed
///     individually so a host can drive the lifecycle while streaming files, reusing one detector instance
///     across runs. Detectors are therefore stateful and must not be invoked concurrently.
/// </remarks>
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
    ///     Resets per-run accumulation state before a new batch begins.
    /// </summary>
    /// <remarks>Call before the first <see cref="AccumulateFile" /> of each detection run.</remarks>
    public void ResetAccumulation() => ResetInternal();

    /// <summary>
    ///     Accumulates pattern signals from a single fused file snapshot.
    /// </summary>
    /// <param name="file">The fused file snapshot to analyze and fold into accumulation state.</param>
    public void AccumulateFile(FusedFileSnapshot file) => AnalyzeFile(file);

    /// <summary>
    ///     Builds the final detected pattern from the accumulated state.
    /// </summary>
    /// <returns>
    ///     The <see cref="DetectedPattern" /> for the run, or <see langword="null" /> when the pattern was not
    ///     detected.
    /// </returns>
    public DetectedPattern? CompleteAccumulation() => BuildResult();

    /// <summary>
    ///     Resets the derived detector's internal accumulation state.
    /// </summary>
    protected abstract void ResetInternal();

    /// <summary>
    ///     Analyzes a single file snapshot and updates the derived detector's accumulation state.
    /// </summary>
    /// <param name="file">The fused file snapshot to analyze.</param>
    protected abstract void AnalyzeFile(FusedFileSnapshot file);

    /// <summary>
    ///     Builds the detected pattern from the derived detector's accumulated state.
    /// </summary>
    /// <returns>
    ///     The <see cref="DetectedPattern" /> for the run, or <see langword="null" /> when the pattern was not
    ///     detected.
    /// </returns>
    protected abstract DetectedPattern? BuildResult();
}
