namespace Fuse.Fusion;

/// <summary>
///     Represents a validation failure that prevents a fusion request from executing.
/// </summary>
public sealed class FusionValidationException : FusionException
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="FusionValidationException" /> class.
    /// </summary>
    /// <param name="errors">The validation errors that were detected.</param>
    public FusionValidationException(IReadOnlyList<string> errors)
        : base(FormatMessage(errors))
    {
        Errors = errors;
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="FusionValidationException" /> class with a single message.
    /// </summary>
    /// <param name="message">The message that describes the validation failure.</param>
    public FusionValidationException(string message)
        : base(message)
    {
        Errors = [message];
    }

    /// <summary>
    ///     Gets the validation errors that were detected.
    /// </summary>
    public IReadOnlyList<string> Errors { get; }

    private static string FormatMessage(IReadOnlyList<string> errors)
    {
        return string.Join(Environment.NewLine, errors);
    }
}
