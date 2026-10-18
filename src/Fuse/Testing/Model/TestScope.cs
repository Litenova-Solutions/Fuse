using Fuse.Paths;

namespace Fuse.Testing.Model;

/// <summary>What a test plan covers: the tests the changes since HEAD affect, or every test in the repository.</summary>
internal abstract record TestScope
{
    private TestScope()
    {
    }

    /// <summary>The tests the working-tree changes can break, as test selection finds them.</summary>
    public sealed record Affected : TestScope
    {
        /// <summary>
        ///     The changed files only other sessions wrote, whose changes the plan leaves out, when it is for one session.
        ///     Empty for a plan for no session, which covers every change.
        /// </summary>
        public IReadOnlySet<RepoPath> LeftOut { get; init; } = new HashSet<RepoPath>();

        /// <summary>
        ///     Absolute paths the plan is limited to: test project files, solution files, or directories holding test
        ///     projects, from <c>dotnet test</c> arguments that only said where the tests are. Empty names every test
        ///     project.
        /// </summary>
        public string[] NamedPaths { get; init; } = [];

        /// <summary>
        ///     True when the arguments named a configuration or a framework, so every run builds with MSBuild and no
        ///     shadow run serves a build the command did not make.
        /// </summary>
        public bool BuildOnly { get; init; }
    }

    /// <summary>Every test project, run whole with no filter, whatever changed.</summary>
    public sealed record All : TestScope;
}
