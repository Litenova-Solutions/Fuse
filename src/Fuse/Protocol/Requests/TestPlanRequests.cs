using Fuse.Protocol.Analysis;
namespace Fuse.Protocol.Requests;

internal abstract partial record EngineRequest
{
    /// <summary>
    ///     The tests the changes since HEAD affect, and the fastest safe way to run each. A plan request always waits for
    ///     the engine to finish loading: <c>fuse test</c> runs nothing until it has the plan, so an answer that says the
    ///     engine is loading would only fail the command.
    /// </summary>
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

        /// <summary>True to clear the slice test cache and run fresh processes (<c>--clean</c>).</summary>
        public bool Clean { get; init; }

        /// <summary>True to bypass the content-addressed slice test cache (<c>--no-cache</c>).</summary>
        public bool NoCache { get; init; }

        /// <summary>Maximum test failures before stopping early; 0 uses the default cap.</summary>
        public int MaxFailures { get; init; }
    }

    /// <summary>Every test project, run whole. Like <see cref="PlanAffectedTests"/>, it always waits for the engine to finish loading.</summary>
    public sealed record PlanAllTests : EngineRequest
    {
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

        /// <summary>True to clear the slice test cache and run fresh processes (<c>--clean</c>).</summary>
        public bool Clean { get; init; }

        /// <summary>True to bypass the content-addressed slice test cache (<c>--no-cache</c>).</summary>
        public bool NoCache { get; init; }

        /// <summary>Maximum test failures before stopping early; 0 uses the default cap.</summary>
        public int MaxFailures { get; init; }
    }

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

        /// <summary>True to clear the slice test cache and run fresh processes (<c>--clean</c>).</summary>
        public bool Clean { get; init; }

        /// <summary>True to bypass the content-addressed slice test cache (<c>--no-cache</c>).</summary>
        public bool NoCache { get; init; }

        /// <summary>Maximum test failures before stopping early; 0 uses the default cap.</summary>
        public int MaxFailures { get; init; }

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

    /// <summary>One failed test of a stored slice verdict.</summary>
    /// <param name="Name">Fully qualified test name.</param>
    /// <param name="Message">Failure message.</param>
    /// <param name="Frames">Stack frames inside the repository.</param>
    public sealed record SliceFailure(string Name, string Message, string[] Frames)
    {
        /// <summary>The test name. A line without it reads as empty.</summary>
        public string Name { get; init; } = Name ?? "";

        /// <summary>The message. A line without it reads as empty.</summary>
        public string Message { get; init; } = Message ?? "";

        /// <summary>The frames. A line without the list reads as one that names none.</summary>
        public string[] Frames { get; init; } = Frames ?? [];

        // A record compares its fields, and an array field by the array itself; two failures with the same frames in
        // different arrays are the same failure, as one read back from the pipe must equal the one that was sent.
        public bool Equals(SliceFailure? other) =>
            other is not null
            && EqualityContract == other.EqualityContract
            && Name == other.Name
            && Message == other.Message
            && Frames.SequenceEqual(other.Frames);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Name);
            hash.Add(Message);
            foreach (var frame in Frames)
                hash.Add(frame);
            return hash.ToHashCode();
        }
    }

    /// <summary>
    ///     Stores one finished test slice's verdict in the slice cache, so the next identical plan reuses a passing
    ///     verdict without running. Answered at once with <see cref="EngineResponse.Acknowledged"/>, without waiting
    ///     for the engine to finish loading; a line with no slice key is ignored.
    /// </summary>
    public sealed record StoreSliceResult : EngineRequest
    {
        private readonly string _sliceKey = "";
        private readonly string _project = "";
        private readonly string _projectName = "";
        private readonly SliceFailure[] _failures = [];

        /// <summary>The slice key the plan gave the run. A line without it reads as empty, which stores nothing.</summary>
        public string SliceKey
        {
            get => _sliceKey;
            init => _sliceKey = value ?? "";
        }

        /// <summary>Absolute path of the test project file. A line without it reads as empty.</summary>
        public string Project
        {
            get => _project;
            init => _project = value ?? "";
        }

        /// <summary>Project name for output. A line without it reads as empty.</summary>
        public string ProjectName
        {
            get => _projectName;
            init => _projectName = value ?? "";
        }

        /// <summary>How many tests passed. A line without it reads as 0.</summary>
        public int Passed { get; init; }

        /// <summary>How many tests failed. A line without it reads as 0.</summary>
        public int Failed { get; init; }

        /// <summary>How many tests were skipped. A line without it reads as 0.</summary>
        public int Skipped { get; init; }

        /// <summary>How long the run took, in milliseconds. A line without it reads as 0.</summary>
        public double DurationMs { get; init; }

        /// <summary>The failures, bounded by the client. A line without the list reads as one that names none.</summary>
        public SliceFailure[] Failures
        {
            get => _failures;
            init => _failures = value ?? [];
        }
    }
}
