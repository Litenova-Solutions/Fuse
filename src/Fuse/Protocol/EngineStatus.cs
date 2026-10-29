namespace Fuse.Protocol;

/// <summary>What <c>fuse status</c> answers: the engine and the tree it watches.</summary>
/// <param name="Pid">The engine process's id.</param>
/// <param name="WorkingSetBytes">The engine process's working set in bytes.</param>
/// <param name="ChangedFileCount">How many sources differ from HEAD.</param>
/// <param name="Head">The commit the baseline is taken from, or null in a repository without commits.</param>
/// <param name="Tree">The tree stamp: 12 lowercase hex characters.</param>
internal sealed record EngineStatus(int Pid, long WorkingSetBytes, int ChangedFileCount, string? Head, string Tree)
{
    /// <summary>The stamp. A line without it reads as empty.</summary>
    public string Tree { get; init; } = Tree ?? "";
}
