namespace Fuse.Emission.Models;

/// <summary>
///     Describes the outcome of consuming tokens against emission budgets.
/// </summary>
public enum BudgetConsumeResult
{
    /// <summary>
    ///     The entry fits in the current output part; emission continues.
    /// </summary>
    Continue,

    /// <summary>
    ///     The entry exceeds the split threshold; the current part should be finalized before writing.
    /// </summary>
    Split,

    /// <summary>
    ///     The hard token limit was reached; emission should stop.
    /// </summary>
    Halt
}
