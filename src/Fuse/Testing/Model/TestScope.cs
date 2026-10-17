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
    }

    /// <summary>Every test project, run whole with no filter, whatever changed.</summary>
    public sealed record All : TestScope;
}
