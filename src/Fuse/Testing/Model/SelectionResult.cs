using Fuse.Paths;

namespace Fuse.Testing.Model;

/// <summary>What test selection found: the selected tests, and how many changed declarations compiled to the same code as HEAD.</summary>
/// <param name="Selections">The selected tests, keyed by test project file.</param>
/// <param name="SameCode">How many changed declarations compiled to the same code as HEAD and selected nothing.</param>
/// <param name="AllSameCode">True when the selector seeded its walks and every changed declaration compiled to the same code as HEAD.</param>
internal sealed record SelectionResult(IReadOnlyDictionary<RepoPath, TestSelection> Selections, int SameCode, bool AllSameCode);
