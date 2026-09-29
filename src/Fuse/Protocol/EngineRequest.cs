using System.Text.Json.Serialization;
using Fuse.Failures;

namespace Fuse.Protocol;

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
[JsonDerivedType(typeof(PlanAffectedTests), nameof(PlanAffectedTests))]
[JsonDerivedType(typeof(PlanAllTests), nameof(PlanAllTests))]
internal abstract record EngineRequest
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

    /// <summary>A liveness probe, answered with <see cref="EngineResponse.Acknowledged"/> once the pipe is up, even while the engine loads.</summary>
    public sealed record Ping : EngineRequest;

    /// <summary>Stops the engine once it has answered with <see cref="EngineResponse.Acknowledged"/>.</summary>
    public sealed record ShutDown : EngineRequest;

    /// <summary>The errors that every change since HEAD introduced, in the changed files and in the files they reach.</summary>
    /// <param name="WaitForLoad">
    ///     True to wait for the engine to finish loading the repository; false to be answered at once with
    ///     <see cref="ErrorCode.Loading"/> while it loads.
    /// </param>
    public sealed record CheckChanges(bool WaitForLoad) : EngineRequest;

    /// <summary>The errors that the changes to the named files introduced, in those files and in the files they reach.</summary>
    /// <param name="Files">
    ///     Absolute or repository-relative paths, usually the files an agent has just edited. The sync reads them from disk
    ///     even before the file watcher reports them, so a check that follows an edit at once still sees it.
    /// </param>
    /// <param name="WaitForLoad">
    ///     True to wait for the engine to finish loading the repository; false to be answered at once with
    ///     <see cref="ErrorCode.Loading"/> while it loads. A line without it reads as false.
    /// </param>
    public sealed record CheckFiles(IReadOnlyList<string> Files, bool WaitForLoad) : EngineRequest
    {
        /// <summary>The named files. A line without the list reads as one that names none, which checks nothing.</summary>
        public IReadOnlyList<string> Files { get; init; } = Files ?? [];
    }

    /// <summary>
    ///     The tests the changes since HEAD affect, and the fastest safe way to run each. A plan request always waits for
    ///     the engine to finish loading: <c>fuse test</c> runs nothing until it has the plan, so an answer that says the
    ///     engine is loading would only fail the command.
    /// </summary>
    public sealed record PlanAffectedTests : EngineRequest;

    /// <summary>Every test project, run whole. Like <see cref="PlanAffectedTests"/>, it always waits for the engine to finish loading.</summary>
    public sealed record PlanAllTests : EngineRequest;
}
