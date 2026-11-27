using Fuse.Graph;
using Microsoft.CodeAnalysis;
namespace Fuse.Testing.Planning;

/// <summary>
///     One test project a plan runs, with its filter and its Roslyn projects per target framework from the pinned
///     generation. A project on Microsoft.Testing.Platform has no variants: it builds with MSBuild.
/// </summary>
/// <param name="Node">The test project, in the order the plan lists it.</param>
/// <param name="Filter">A VSTest filter expression, or null to run every test in the project.</param>
/// <param name="FilterArguments">The filter arguments for a run on Microsoft.Testing.Platform, in its framework's spelling.</param>
/// <param name="Variants">The Roslyn project per target framework from the pinned generation.</param>
internal sealed record PlannedProjectWork(ProjectNode Node, string? Filter, string[] FilterArguments, IReadOnlyList<Project> Variants);
