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
[JsonDerivedType(typeof(CheckPaths), nameof(CheckPaths))]
[JsonDerivedType(typeof(BeginSession), nameof(BeginSession))]
[JsonDerivedType(typeof(EndSession), nameof(EndSession))]
[JsonDerivedType(typeof(PlanAffectedTests), nameof(PlanAffectedTests))]
[JsonDerivedType(typeof(PlanAllTests), nameof(PlanAllTests))]
[JsonDerivedType(typeof(PlanFailedTests), nameof(PlanFailedTests))]
[JsonDerivedType(typeof(ReportTestResults), nameof(ReportTestResults))]
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

    /// <summary>
    ///     How long the client waits for the answer, in milliseconds, which the client sets on every request it sends; 0
    ///     when it did not say. A check stops its analyzers before then and answers with what finished, so a client is
    ///     never left with no answer because an analyzer was slow.
    /// </summary>
    public long WaitMs { get; init; }

    /// <summary>A liveness probe, answered with <see cref="EngineResponse.Acknowledged"/> once the pipe is up, even while the engine loads.</summary>
    public sealed record Ping : EngineRequest;

    /// <summary>Stops the engine once it has answered with <see cref="EngineResponse.Acknowledged"/>.</summary>
    public sealed record ShutDown : EngineRequest;

    /// <summary>The errors that every change since HEAD introduced, in the changed files and in the files they reach.</summary>
    /// <param name="WaitForLoad">
    ///     True to wait for the engine to finish loading the repository; false to be answered at once with
    ///     <see cref="ErrorCode.Loading"/> while it loads.
    /// </param>
    public sealed record CheckChanges(bool WaitForLoad) : EngineRequest
    {
        /// <summary>
        ///     The session the answer is for, as its harness identifies it, or null to answer with every error. The stop
        ///     hook sends the session that is stopping, which is told the errors its own edits cause and those no session's
        ///     edits are credited with, and not those other sessions' edits cause without it.
        /// </summary>
        public string? Session { get; init; }

        /// <summary>
        ///     True when <see cref="Session"/> is stopping, which the stop hook sends. A subagent that has stopped writes no
        ///     more, so from then on its parent session counts its edits as its own. A line without it reads as false.
        /// </summary>
        public bool IsStopping { get; init; }

        /// <summary>
        ///     How many errors the answer holds at most; 0 is the existing cap. A line without it reads as 0.
        /// </summary>
        public int MaxErrors { get; init; }
    }

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

        /// <summary>
        ///     The session that wrote <see cref="Files"/>, as its harness identifies it, or null when the client knows none.
        ///     The engine records that the session wrote the files, and answers with the errors that session's edits cause,
        ///     leaving out those other sessions' edits caused without it. Only the post-edit hook sends one.
        /// </summary>
        public string? Session { get; init; }

        /// <summary>
        ///     True to leave out the analyzers that read the whole compilation (compilation-wide analyzers), which the
        ///     check then names. The post-edit hook sends true, so an edit is answered in seconds; the stop hook, which
        ///     checks every change, runs them. A line without it reads as false, which runs every analyzer.
        /// </summary>
        public bool LeaveOutCompilationWide { get; init; }

        /// <summary>
        ///     How many errors the answer holds at most; 0 is the existing cap. A line without it reads as 0.
        /// </summary>
        public int MaxErrors { get; init; }
    }

    /// <summary>
    ///     The tests the changes since HEAD affect, and the fastest safe way to run each. A plan request always waits for
    ///     the engine to finish loading: <c>fuse test</c> runs nothing until it has the plan, so an answer that says the
    ///     engine is loading would only fail the command.
    /// </summary>
    /// <summary>The errors that the changes under the named paths introduced, in those files and in the files they reach.</summary>
    /// <param name="Paths">Absolute file and directory paths and absolute glob patterns.</param>
    /// <param name="WaitForLoad">True to wait for the engine to finish loading; false to be answered at once with <see cref="ErrorCode.Loading"/> while it loads.</param>
    public sealed record CheckPaths(string[] Paths, bool WaitForLoad) : EngineRequest
    {
        /// <summary>The named paths. A line without the list reads as one that names none, which checks nothing.</summary>
        public string[] Paths { get; init; } = Paths ?? [];

        /// <summary>
        ///     How many errors the answer holds at most; 0 is the existing cap. A line without it reads as 0.
        /// </summary>
        public int MaxErrors { get; init; }
    }

    /// <summary>Registers an external writer as a session for the path patterns it writes.</summary>
    /// <param name="Name">The name the launcher gives the writer.</param>
    /// <param name="Paths">Repository-relative path patterns, as the launcher named them.</param>
    public sealed record BeginSession(string Name, string[] Paths) : EngineRequest
    {
        /// <summary>The name. A line without it reads as empty, which no registration holds.</summary>
        public string Name { get; init; } = Name ?? "";

        /// <summary>The patterns. A line without the list reads as one that names none.</summary>
        public string[] Paths { get; init; } = Paths ?? [];
    }

    /// <summary>Ends the registration an earlier begin request made.</summary>
    /// <param name="Name">The name the launcher gave the writer.</param>
    public sealed record EndSession(string Name) : EngineRequest
    {
        /// <summary>The name. A line without it reads as empty, which no registration holds.</summary>
        public string Name { get; init; } = Name ?? "";
    }

    public sealed record PlanAffectedTests : EngineRequest
    {
        /// <summary>
        ///     The session the plan is for, as its harness identifies it, or null to cover every change. With one, the plan
        ///     leaves out the changes only other sessions wrote, so a subagent runs the tests its own edits affect. The
        ///     pre-shell hook passes it to <c>fuse test</c>.
        /// </summary>
        public string? Session { get; init; }

        private readonly string[] _projects = [];

        /// <summary>
        ///     Absolute paths the plan is limited to: test project files, solution files, or directories holding test
        ///     projects, from <c>dotnet test</c> arguments that only said where the tests are. Empty names every test
        ///     project. A line without the list reads as one that names none.
        /// </summary>
        public string[] Projects
        {
            get => _projects;
            init => _projects = value ?? [];
        }

        /// <summary>
        ///     True when the arguments named a configuration or a framework, so every run builds with MSBuild and no
        ///     shadow run serves a build the command did not make. A line without it reads as false.
        /// </summary>
        public bool BuildOnly { get; init; }
    }

    /// <summary>Every test project, run whole. Like <see cref="PlanAffectedTests"/>, it always waits for the engine to finish loading.</summary>
    public sealed record PlanAllTests : EngineRequest;

    /// <summary>One test project of a rerun, with the fully qualified names of the tests to run in it.</summary>
    /// <param name="Project">Absolute path of the test project file, as the last run recorded it.</param>
    /// <param name="Tests">The failed test names to rerun, distinct and in ordinal order.</param>
    public sealed record FailedProject(string Project, string[] Tests)
    {
        /// <summary>The project file. A line without it reads as empty, which names no project.</summary>
        public string Project { get; init; } = Project ?? "";

        /// <summary>The tests. A line without the list reads as one that names none.</summary>
        public string[] Tests { get; init; } = Tests ?? [];

        // A record compares its fields, and an array field by the array itself; two projects with the same names in
        // different arrays are the same project, as one read back from the pipe must equal the one that was sent.
        public bool Equals(FailedProject? other) =>
            other is not null
            && EqualityContract == other.EqualityContract
            && Project == other.Project
            && Tests.SequenceEqual(other.Tests);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Project);
            foreach (var test in Tests)
                hash.Add(test);
            return hash.ToHashCode();
        }
    }

    /// <summary>The tests that failed in the last recorded run, each project filtered to its recorded names. Like the other plan requests, it always waits for the engine to finish loading.</summary>
    public sealed record PlanFailedTests(FailedProject[] Projects) : EngineRequest
    {
        /// <summary>The projects to rerun. A line without the list reads as one that names none, which runs nothing.</summary>
        public FailedProject[] Projects { get; init; } = Projects ?? [];

        public bool Equals(PlanFailedTests? other) =>
            other is not null
            && EqualityContract == other.EqualityContract
            && Projects.SequenceEqual(other.Projects);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var project in Projects)
                hash.Add(project);
            return hash.ToHashCode();
        }
    }

    /// <summary>Every test outcome of the shadow runs of one plan, so the engine keeps the classes that passed.</summary>
    public sealed record ReportTestResults(ReportedTestResult[] Results) : EngineRequest
    {
        /// <summary>The outcomes. A line without the list reads as one that names none, which keeps nothing.</summary>
        public ReportedTestResult[] Results { get; init; } = Results ?? [];

        private readonly string _planId = "";

        /// <summary>
        ///     The plan the outcomes ran from, which names the content they ran against. A line without it reads as
        ///     empty, which no plan has.
        /// </summary>
        public string PlanId
        {
            get => _planId;
            init => _planId = value ?? "";
        }

        public bool Equals(ReportTestResults? other) =>
            other is not null
            && EqualityContract == other.EqualityContract
            && PlanId == other.PlanId
            && Results.SequenceEqual(other.Results);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(PlanId);
            foreach (var result in Results)
                hash.Add(result);
            return hash.ToHashCode();
        }
    }
}
