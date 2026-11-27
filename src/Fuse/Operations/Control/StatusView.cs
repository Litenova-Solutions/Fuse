using Fuse.Protocol.Analysis;
namespace Fuse.Operations.Control;

/// <summary>What <c>fuse status</c> reports: the engine, then the tree, then the limits counted as the code counts them.</summary>
internal sealed record StatusView(
    string Engine,
    int? Pid,
    int? MemoryMB,
    int ChangedFiles,
    string? Head,
    string Tree,
    int? ChangedSourcesInReach,
    int? TouchedPaths,
    int TouchedLimit,
    int KeptAnswerAt,
    int WholeProjectAt,
    int SyncReloadAt,
    int ReloadAt,
    long? ReloadMs,
    string? GateHolder,
    long? MemoryCeilingBytes,
    long? MemoryLoadBytes,
    long? MemoryWorkingSetBytes,
    string? PipeName = null,
    string? BuildId = null)
{
    /// <summary>The live session registrations, named without the stored prefix. Empty when none is live.</summary>
    public ReportedSession[] Sessions { get; init; } = [];
}
