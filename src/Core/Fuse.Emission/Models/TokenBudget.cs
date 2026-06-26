namespace Fuse.Emission.Models;

/// <summary>
///     Tracks token consumption against <see cref="EmissionOptions.MaxTokens" /> and
///     <see cref="EmissionOptions.SplitTokens" /> during emission.
/// </summary>
public sealed class TokenBudget
{
    private readonly int? _maxTokens;
    private readonly int? _splitTokens;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TokenBudget" /> class.
    /// </summary>
    /// <param name="options">The emission options defining token limits.</param>
    public TokenBudget(EmissionOptions options)
    {
        _maxTokens = options.MaxTokens;
        _splitTokens = options.SplitTokens;
    }

    /// <summary>
    ///     A value indicating whether the hard <see cref="EmissionOptions.MaxTokens" /> limit was reached.
    /// </summary>
    public bool IsExhausted { get; private set; }

    /// <summary>
    ///     The number of tokens consumed in the current output part.
    /// </summary>
    public long CurrentPartTokens { get; private set; }

    /// <summary>
    ///     The total number of tokens consumed across all output parts.
    /// </summary>
    public long TotalTokens { get; private set; }

    /// <summary>
    ///     Evaluates whether an entry can be added to the current part and records its token cost when allowed.
    /// </summary>
    /// <param name="tokens">The token cost of the entry, including marker overhead.</param>
    /// <returns>
    ///     <see cref="BudgetConsumeResult.Split" /> when the current part must be finalized first,
    ///     <see cref="BudgetConsumeResult.Halt" /> when the hard limit would be exceeded,
    ///     or <see cref="BudgetConsumeResult.Continue" /> when the entry was accepted.
    /// </returns>
    /// <remarks>
    ///     The token cost is committed only when the result is <see cref="BudgetConsumeResult.Continue" />. A
    ///     <see cref="BudgetConsumeResult.Halt" /> rejects the entry without charging it, so a caller that
    ///     declines to write the rejected entry leaves <see cref="TotalTokens" /> reflecting only what was
    ///     emitted. This is what lets emission stop at the hard limit without overshooting by one entry.
    /// </remarks>
    public BudgetConsumeResult Consume(int tokens)
    {
        if (IsExhausted)
        {
            return BudgetConsumeResult.Halt;
        }

        if (_splitTokens.HasValue &&
            CurrentPartTokens > 0 &&
            CurrentPartTokens + tokens > _splitTokens.Value)
        {
            return BudgetConsumeResult.Split;
        }

        // Reject before committing when the entry would breach the hard limit, so the over-budget entry is
        // never charged and never written. Previously the cost was added first and the limit checked after,
        // which let the entry that crossed MaxTokens still be emitted.
        if (_maxTokens.HasValue && TotalTokens + tokens > _maxTokens.Value)
        {
            IsExhausted = true;
            return BudgetConsumeResult.Halt;
        }

        CurrentPartTokens += tokens;
        TotalTokens += tokens;
        return BudgetConsumeResult.Continue;
    }

    /// <summary>
    ///     Records token consumption without evaluating the split threshold.
    /// </summary>
    /// <param name="tokens">The token cost of the entry, including marker overhead.</param>
    /// <returns>
    ///     <see cref="BudgetConsumeResult.Halt" /> when the hard limit is exceeded,
    ///     otherwise <see cref="BudgetConsumeResult.Continue" />.
    /// </returns>
    public BudgetConsumeResult ForceConsume(int tokens)
    {
        CurrentPartTokens += tokens;
        TotalTokens += tokens;

        if (_maxTokens.HasValue && TotalTokens > _maxTokens.Value)
        {
            IsExhausted = true;
            return BudgetConsumeResult.Halt;
        }

        return BudgetConsumeResult.Continue;
    }

    /// <summary>
    ///     Resets the token count for the current output part after a split.
    /// </summary>
    public void ResetCurrentPart()
    {
        CurrentPartTokens = 0;
    }
}
