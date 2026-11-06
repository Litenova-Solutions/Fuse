namespace Fuse.Protocol;

/// <summary>What <c>fuse status</c> answers: the engine and the tree it watches.</summary>
/// <param name="Pid">The engine process's id.</param>
/// <param name="WorkingSetBytes">The engine process's working set in bytes.</param>
/// <param name="ChangedFileCount">How many changed C# sources differ from HEAD.</param>
/// <param name="Head">The commit the baseline is taken from, or null in a repository without commits.</param>
/// <param name="Tree">The tree stamp: 12 lowercase hex characters.</param>
internal sealed record EngineStatus(int Pid, long WorkingSetBytes, int ChangedFileCount, string? Head, string Tree)
{
    /// <summary>The stamp. A line without it reads as empty.</summary>
    public string Tree { get; init; } = Tree ?? "";

    /// <summary>How many changed C# sources a project owns, counted as the graph counts owners. A line without it reads as 0.</summary>
    public int ChangedSourcesInReach { get; init; }

    /// <summary>How many distinct paths the views have applied, against the limit past which projects re-evaluate. A line without it reads as 0.</summary>
    public int TouchedCount { get; init; }

    /// <summary>How long the last completed sync's reload took in milliseconds, or null when that sync patched files without reloading.</summary>
    public long? ReloadMs { get; init; }

    /// <summary>What holds the request lock while this answer is read from the last completed sync, or null when it is read from a sync it ran.</summary>
    public string? GateHolder { get; init; }
}
