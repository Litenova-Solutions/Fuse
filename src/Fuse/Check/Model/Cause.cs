namespace Fuse.Check.Model;

/// <summary>
///     The declaration change that made a candidate a candidate. It is printed under an error in that file, so an agent
///     reading an error in a file it did not edit sees which of its edits broke it.
/// </summary>
internal abstract record Cause
{
    private Cause(string declaration) => Declaration = declaration;

    /// <summary>The declaration's header on one line, without its body or its members.</summary>
    public string Declaration { get; }

    /// <summary>A declaration the working tree has, changed or added, quoted as the working tree has it.</summary>
    public sealed record Changed : Cause
    {
        public Changed(string declaration)
            : base(declaration)
        {
        }
    }

    /// <summary>
    ///     A declaration the working tree no longer has, quoted as it was at HEAD. A rename is a removal of the old name,
    ///     and the errors it causes name the old one, so its cause is this case.
    /// </summary>
    public sealed record Removed : Cause
    {
        public Removed(string declaration)
            : base(declaration)
        {
        }
    }
}
