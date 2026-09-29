using Fuse.Graph;
using Fuse.Paths;
using Fuse.Testing.Model;

namespace Fuse.Testing;

/// <summary>
///     The selection of each test project while one walk is still finding tests. A project selected whole runs every
///     test, so a pattern found for it, before or after, adds nothing to it.
/// </summary>
internal sealed class SelectionBuilder
{
    private readonly Dictionary<RepoPath, TestSelection> _selections = [];

    /// <summary>
    ///     The selections found so far, keyed by test project file, in the order the walk first selected something in
    ///     each project. It is a view: it grows while the walk goes on.
    /// </summary>
    public IReadOnlyDictionary<RepoPath, TestSelection> Selections => _selections;

    /// <summary>True when <paramref name="project"/> is selected whole.</summary>
    public bool IsWhole(ProjectNode project) => _selections.GetValueOrDefault(project.Path) is TestSelection.Whole;

    /// <summary>Selects every test in <paramref name="project"/>. A reason given later replaces an earlier one.</summary>
    public void SelectWhole(ProjectNode project, string reason) => _selections[project.Path] = new TestSelection.Whole(reason);

    /// <summary>Selects the tests in <paramref name="project"/> whose fully qualified name contains <paramref name="pattern"/>.</summary>
    /// <returns>
    ///     False when the project is whole or already has the pattern, so the caller can skip the work a new pattern
    ///     would start.
    /// </returns>
    public bool Add(ProjectNode project, string pattern)
    {
        switch (_selections.GetValueOrDefault(project.Path))
        {
            case TestSelection.Whole:
                return false;
            case TestSelection.Methods methods:
                if (methods.Patterns.Contains(pattern))
                    return false;
                _selections[project.Path] = methods with { Patterns = methods.Patterns.Add(pattern) };
                return true;
            default:
                _selections[project.Path] = new TestSelection.Methods([pattern]);
                return true;
        }
    }
}
