namespace Fuse.Testing.Model;

/// <summary>The VSTest filter a selection passes to <c>dotnet test</c>, and whether it collapsed to fit the bound.</summary>
/// <param name="Expression">A VSTest filter expression, or null to run every test in the project.</param>
/// <param name="Patterns">How many <c>FullyQualifiedName~</c> patterns <paramref name="Expression"/> has; 0 when it is null.</param>
/// <param name="Collapse">Whether the selection had more patterns than the bound, and what the filter collapsed to.</param>
internal sealed record RunFilter(string? Expression, int Patterns, FilterCollapse Collapse);
