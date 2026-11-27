namespace Fuse.Identity;

/// <summary>
///     How many errors a command shows: the command's default count, a count the user asked for with
///     <c>--max-errors</c>, or every error (<c>--all-errors</c>). The wire carries it as a count
///     (<see cref="ToCount"/>), 0 for the default and <see cref="int.MaxValue"/> for every error.
/// </summary>
internal abstract record ErrorLimit
{
    private ErrorLimit()
    {
    }

    /// <summary>The command's default count.</summary>
    public static ErrorLimit Default { get; } = new DefaultCount();

    /// <summary>Every error.</summary>
    public static ErrorLimit All { get; } = new Every();

    /// <summary>The limit a count on the wire stands for: 0 is the default, <see cref="int.MaxValue"/> every error.</summary>
    public static ErrorLimit FromCount(int count) => count switch
    {
        <= 0 => Default,
        int.MaxValue => All,
        _ => new UpTo(count),
    };

    /// <summary>The count the wire carries: 0 for the default, <see cref="int.MaxValue"/> for every error.</summary>
    public abstract int ToCount();

    /// <summary>The command's default count.</summary>
    public sealed record DefaultCount : ErrorLimit
    {
        public override int ToCount() => 0;
    }

    /// <summary>At most <paramref name="Count"/> errors, as <c>--max-errors</c> asked.</summary>
    /// <param name="Count">The most errors to show, 1 or more.</param>
    public sealed record UpTo(int Count) : ErrorLimit
    {
        public override int ToCount() => Count;
    }

    /// <summary>Every error, as <c>--all-errors</c> asked.</summary>
    public sealed record Every : ErrorLimit
    {
        public override int ToCount() => int.MaxValue;
    }
}
