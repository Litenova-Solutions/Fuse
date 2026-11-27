using System.Text.Json.Serialization;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Transport;
namespace Fuse.Protocol.Requests;

/// <summary>
///     One request from a client to the engine. Each pipe connection carries exactly one. The JSON names the case in a
///     <c>request</c> property, written first, which System.Text.Json reads to choose the record to create.
/// </summary>
/// <remarks>
///     The engine reads <see cref="BuildId"/> before the case (<see cref="ProtocolJson.ReadBuildId"/>), so a client of any
///     other build gets <see cref="EngineResponse.Restart"/>, whatever shape that build gives its requests. The case
///     property is not called <c>kind</c>: an engine of an earlier build reads <c>kind</c> into its own enum, and it drops
///     a request whose value it does not know instead of answering Restart.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "request")]
[JsonDerivedType(typeof(Ping), nameof(Ping))]
[JsonDerivedType(typeof(ShutDown), nameof(ShutDown))]
[JsonDerivedType(typeof(CheckChanges), nameof(CheckChanges))]
[JsonDerivedType(typeof(CheckFiles), nameof(CheckFiles))]
[JsonDerivedType(typeof(CheckPaths), nameof(CheckPaths))]
[JsonDerivedType(typeof(BeginSession), nameof(BeginSession))]
[JsonDerivedType(typeof(EndSession), nameof(EndSession))]
[JsonDerivedType(typeof(PlanAffectedTests), nameof(PlanAffectedTests))]
[JsonDerivedType(typeof(PlanAllTests), nameof(PlanAllTests))]
[JsonDerivedType(typeof(PlanFailedTests), nameof(PlanFailedTests))]
[JsonDerivedType(typeof(StoreSliceResult), nameof(StoreSliceResult))]
[JsonDerivedType(typeof(PlanChangedBuild), nameof(PlanChangedBuild))]
[JsonDerivedType(typeof(Status), nameof(Status))]
[JsonDerivedType(typeof(YieldMemory), nameof(YieldMemory))]
[JsonDerivedType(typeof(Judge), nameof(Judge))]
[JsonDerivedType(typeof(Graph), nameof(Graph))]
[JsonDerivedType(typeof(Impact), nameof(Impact))]
[JsonDerivedType(typeof(SessionGuard), nameof(SessionGuard))]
[JsonDerivedType(typeof(WatchStream), nameof(WatchStream))]
internal abstract partial record EngineRequest
{
    private readonly string _buildId = "";
    private readonly string _requestId = "";

    private EngineRequest()
    {
    }

    /// <summary>
    ///     The client's <see cref="EngineVersion.Build"/>, which the client sets on every request it sends. An engine of
    ///     another build answers <see cref="EngineResponse.Restart"/> and exits.
    /// </summary>
    /// <remarks>
    ///     A line without it reads as empty rather than null: the source-generated reader assigns every init-only property,
    ///     a missing one as null, which would override an initializer. The same holds for <see cref="RequestId"/>.
    /// </remarks>
    public string BuildId
    {
        get => _buildId;
        init => _buildId = value ?? "";
    }

    /// <summary>
    ///     Names this request in the engine's log, so a measurement can find the line that belongs to its own call. A
    ///     request without one, or with an empty one, gets no phase line.
    /// </summary>
    public string RequestId
    {
        get => _requestId;
        init => _requestId = value ?? "";
    }

    /// <summary>
    ///     How long the client waits for the answer, in milliseconds, which the client sets on every request it sends; 0
    ///     when it did not say. A check stops its analyzers before then and answers with what finished, so a client is
    ///     never left with no answer because an analyzer was slow.
    /// </summary>
    public long WaitMs { get; init; }

    /// <summary>
    ///     Canonical root path of the caller's git worktree, so a hypervisor shared across worktrees knows which
    ///     worktree overlay to check or test against. Null when omitted by an older client.
    /// </summary>
    public string? WorktreeRoot { get; init; }

    /// <summary>A liveness probe, answered with <see cref="EngineResponse.Acknowledged"/> once the pipe is up, even while the engine loads.</summary>
    public sealed record Ping : EngineRequest;

    /// <summary>Stops the engine once it has answered with <see cref="EngineResponse.Acknowledged"/>.</summary>
    public sealed record ShutDown : EngineRequest;

    /// <summary>
    ///     The engine and the tree it watches: the process, its working set, and the changed files with their stamp.
    ///     Answered without requiring C# projects, so status works in a repository that has none.
    /// </summary>
    public sealed record Status : EngineRequest;

    /// <summary>
    ///     Asks the engine to shed transient caches and run a compacting Gen2 GC before a heavy external build
    ///     (<c>fuse slim</c>), answered with <see cref="EngineResponse.MemoryYielded"/>.
    /// </summary>
    public sealed record YieldMemory : EngineRequest;
}
