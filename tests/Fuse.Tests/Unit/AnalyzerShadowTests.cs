using System.Reflection;
using Fuse.Tests.Fixtures;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fuse.Tests.Unit;

/// <summary>
///     The engine loads a repository's own analyzers from a copy, so a real build can still replace them. These cases are
///     the rules that make the copy safe: only files inside the repository are copied, the copy is under the state
///     directory, and the same solution is shadowed once, so Roslyn's cached compilations survive.
/// </summary>
public class AnalyzerShadowTests
{
    [Fact]
    public void An_in_repository_analyzer_points_at_a_copy_and_a_package_analyzer_stays_put()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var inRepo = repo.Full("Gen/bin/Gen.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(inRepo)!);
        File.Copy(typeof(AnalyzerShadowTests).Assembly.Location, inRepo);
        var outside = typeof(object).Assembly.Location;
        var solution = SolutionWith(inRepo, outside);

        var shadowed = new AnalyzerShadow(repo.Root).Apply(solution);

        var paths = shadowed.Projects.Single().AnalyzerReferences.Select(r => r.FullPath!).ToList();
        Assert.Contains(outside, paths);
        var copy = Assert.Single(paths, p => p != outside);
        Assert.StartsWith(Path.Combine(repo.Root.StateDirectory, "analyzers"), copy, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(File.ReadAllBytes(inRepo), File.ReadAllBytes(copy));
    }

    [Fact]
    public void The_same_solution_is_shadowed_once()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var inRepo = repo.Full("Gen/bin/Gen.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(inRepo)!);
        File.Copy(typeof(AnalyzerShadowTests).Assembly.Location, inRepo);
        var solution = SolutionWith(inRepo);
        var shadow = new AnalyzerShadow(repo.Root);

        // A new result object for the same input would carry no compilation Roslyn had already computed.
        Assert.Same(shadow.Apply(solution), shadow.Apply(solution));
    }

    private static Solution SolutionWith(params string[] analyzers)
    {
        var loader = new PathLoader();
        var project = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "P", "P", LanguageNames.CSharp)
            .WithAnalyzerReferences(analyzers.Select(a => (AnalyzerReference)new AnalyzerFileReference(a, loader)));
        using var workspace = new AdhocWorkspace();
        return workspace.CurrentSolution.AddProject(project);
    }

    /// <summary>Stands in for Roslyn's loader; these cases never load an analyzer, they only move its reference.</summary>
    private sealed class PathLoader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath)
        {
        }

        public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);
    }
}
