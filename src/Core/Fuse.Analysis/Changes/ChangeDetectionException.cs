namespace Fuse.Analysis.Changes;

/// <summary>
///     Thrown when git change detection fails.
/// </summary>
public sealed class ChangeDetectionException : Exception
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ChangeDetectionException" /> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    public ChangeDetectionException(string message)
        : base(message)
    {
    }
}
