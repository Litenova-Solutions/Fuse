namespace Fuse.Cli.Services;

/// <summary>
///     Defines the contract for console user interface operations.
/// </summary>
public interface IConsoleUI
{
    /// <summary>
    ///     Writes a success message to the console.
    /// </summary>
    /// <param name="message">The success message to display.</param>
    void WriteSuccess(string message);

    /// <summary>
    ///     Writes an error message to the console.
    /// </summary>
    /// <param name="message">The error message to display.</param>
    void WriteError(string message);

    /// <summary>
    ///     Writes a step or progress message to the console.
    /// </summary>
    /// <param name="message">The step message to display.</param>
    void WriteStep(string message);

    /// <summary>
    ///     Writes a result message to the console.
    /// </summary>
    /// <param name="message">The result message to display.</param>
    void WriteResult(string message);
}
