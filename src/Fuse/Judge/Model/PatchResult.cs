namespace Fuse.Judge.Model;

/// <summary>What applying one file patch gives: the new text, or why the hunks do not match the file.</summary>
internal abstract record PatchResult
{
    private PatchResult()
    {
    }

    /// <summary>The patch applied.</summary>
    /// <param name="Text">The file's new text.</param>
    public sealed record Applied(string Text) : PatchResult;

    /// <summary>The patch does not apply.</summary>
    /// <param name="Reason">Why the hunks do not match the file.</param>
    public sealed record Conflicted(string Reason) : PatchResult;
}
