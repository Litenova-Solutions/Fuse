namespace Fuse.Changes.Model;

/// <summary>
///     One declaration in the surface of a file that was added, removed or changed between HEAD and the working tree.
///     Its text is the declaration's header as written, on one line, which is what a cause quotes.
/// </summary>
internal abstract record DeclarationChange
{
    private DeclarationChange(DeclarationKey key, IReadOnlyList<string> names)
    {
        Key = key;
        Names = names;
    }

    /// <summary>The declaration's identity within the file.</summary>
    public DeclarationKey Key { get; }

    /// <summary>
    ///     Identifiers other code would use to reach the declaration: its own name and its containing type's name, or
    ///     for a using directive, the names of the types the file declares. They come from the working tree, or from HEAD
    ///     for a removal. A file-level attribute list has none.
    /// </summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>A declaration only the working tree has.</summary>
    public sealed record Added : DeclarationChange
    {
        public Added(DeclarationKey key, IReadOnlyList<string> names, string after)
            : base(key, names) =>
            After = after;

        /// <summary>The declaration as the working tree has it.</summary>
        public string After { get; }
    }

    /// <summary>A declaration only HEAD has. A rename is the removal of the old name and the addition of the new one.</summary>
    public sealed record Removed : DeclarationChange
    {
        public Removed(DeclarationKey key, IReadOnlyList<string> names, string before)
            : base(key, names) =>
            Before = before;

        /// <summary>The declaration as HEAD has it.</summary>
        public string Before { get; }
    }

    /// <summary>A declaration both versions have, whose surface differs: its signature, its accessibility, or a constant's value.</summary>
    public sealed record Changed : DeclarationChange
    {
        public Changed(DeclarationKey key, IReadOnlyList<string> names, string before, string after)
            : base(key, names)
        {
            Before = before;
            After = after;
        }

        /// <summary>The declaration as HEAD has it.</summary>
        public string Before { get; }

        /// <summary>The declaration as the working tree has it.</summary>
        public string After { get; }
    }
}
