using System.Collections.Immutable;
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

    public bool IsWhole(ProjectNode project) => _selections.GetValueOrDefault(project.Path) is TestSelection.Whole;

    /// <summary>Selects every test in <paramref name="project"/>. A reason given later replaces an earlier one.</summary>
    public void SelectWhole(ProjectNode project, string reason) => _selections[project.Path] = new TestSelection.Whole(reason);

    /// <summary>
    ///     Selects the tests in <paramref name="project"/> whose fully qualified name contains <paramref name="pattern"/>.
    ///     A class pattern, which ends with a dot, covers the test names that start with it, so a name it covers is not
    ///     added and a new class pattern replaces the names it covers.
    /// </summary>
    /// <returns>
    ///     False when the project is whole or already has the pattern, so the caller can skip the work a new pattern
    ///     would start. A test name a class pattern covers still returns true, since the name itself is new.
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
                if (methods.Patterns.Any(p => Covers(p, pattern)))
                    return true;
                var patterns = methods.Patterns.Where(p => !Covers(pattern, p)).ToImmutableHashSet().Add(pattern);
                _selections[project.Path] = methods with { Patterns = patterns };
                return true;
            default:
                _selections[project.Path] = new TestSelection.Methods([pattern]);
                return true;
        }
    }

    private static bool Covers(string classPattern, string pattern) =>
        classPattern.EndsWith('.') && pattern.Length > classPattern.Length && pattern.StartsWith(classPattern, StringComparison.Ordinal);
}
