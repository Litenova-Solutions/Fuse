namespace Fuse.Fusion;

/// <summary>
///     Represents an error that occurs during a fusion operation.
/// </summary>
public class FusionException : Exception
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="FusionException" /> class.
    /// </summary>
    public FusionException()
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="FusionException" /> class with a specified message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public FusionException(string message)
        : base(message)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="FusionException" /> class with a specified message
    ///     and a reference to the inner exception that caused this exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused the current exception.</param>
    public FusionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
