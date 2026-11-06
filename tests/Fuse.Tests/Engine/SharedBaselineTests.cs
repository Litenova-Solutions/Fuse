using System.Collections.Concurrent;
using System.Reflection;
using Fuse.Check;
using Fuse.Check.Model;
using Fuse.Paths;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fuse.Tests.Engine;

/// <summary>
///     The worktrees of one repository share one baseline store and one analyzer copy directory, keyed by the git
///     common directory, while each worktree keeps its own engine, sessions, build lock and logs. Worktrees on the
///     same commit compute HEAD's baseline once, and concurrent writes from their engines leave complete results.
/// </summary>
public class SharedBaselineTests
{
    [Fact]
    public void Worktrees_of_one_repository_share_one_baseline_directory()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var worktree = AddWorktree(repo);
        try
        {
            var other = RepoRoot.Find(worktree) ?? throw new InvalidOperationException($"no repository at {worktree}");

            Assert.Equal(repo.Root.GitCommonDirectory, other.GitCommonDirectory);
            Assert.Equal(repo.Root.SharedStateDirectory, other.SharedStateDirectory);
            Assert.NotEqual(repo.Root.StateDirectory, other.StateDirectory);
        }
        finally
        {
            RemoveWorktree(repo, worktree);
        }
    }

    [Fact]
    public void Separate_repositories_do_not_share_a_baseline_directory()
    {
        using var first = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        using var second = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });

        Assert.NotEqual(first.Root.SharedStateDirectory, second.Root.SharedStateDirectory);
    }

    [Fact]
    public void The_key_names_in_repository_paths_by_their_relative_path()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var outside = Path.Combine(Path.GetTempPath(), "fuse-tests", "outside-" + Guid.NewGuid().ToString("N")[..8] + ".dll");

        Assert.Equal("Gen/Gen.dll", BaselineStore.KeyPathFor(repo.Root, repo.Full("Gen/Gen.dll")));
        Assert.Equal(outside, BaselineStore.KeyPathFor(repo.Root, outside));
    }

    [Fact]
    public void The_key_ignores_worktree_paths_inside_restore_output()
    {
        using var first = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        using var second = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        first.Write("Lib/obj/project.assets.json", AssetsFor(first.Root.Path));
        second.Write("Lib/obj/project.assets.json", AssetsFor(second.Root.Path));

        var firstHash = BaselineStore.HashAssets(first.Root, first.Full("Lib/obj/project.assets.json"));
        var secondHash = BaselineStore.HashAssets(second.Root, second.Full("Lib/obj/project.assets.json"));
        Assert.NotNull(firstHash);
        Assert.NotNull(secondHash);
        Assert.Equal(firstHash, secondHash);

        second.Write("Lib/obj/project.assets.json", AssetsFor(second.Root.Path).Replace("Lib/1.0.0", "Lib/2.0.0", StringComparison.Ordinal));
        Assert.NotEqual(firstHash, BaselineStore.HashAssets(second.Root, second.Full("Lib/obj/project.assets.json")));
    }

    [Fact]
    public async Task Two_worktrees_on_the_same_commit_compute_the_baseline_once()
    {
        using var repo = FixtureRepo.CreateStandard();
        repo.Replace("Lib/Calc.cs", "a * b;", "a * existingError;");
        repo.Commit("an error at HEAD");
        var worktree = AddWorktree(repo);
        try
        {
            FixtureRepo.Run(worktree, "dotnet", "restore", FixtureTemplate.SolutionFile, "-nologo", "-v:q");
            var other = RepoRoot.Find(worktree) ?? throw new InvalidOperationException($"no repository at {worktree}");
            repo.Replace("Lib/Calc.cs", "a + b;", "a + mainError;");
            var worktreeCalc = Path.Combine(worktree, "Lib", "Calc.cs");
            File.WriteAllText(worktreeCalc, File.ReadAllText(worktreeCalc).Replace("a + b;", "a + worktreeError;", StringComparison.Ordinal));
            var head = FixtureRepo.Run(repo.Root.Path, "git", "rev-parse", "HEAD").Trim();
            var cancellationToken = TestContext.Current.CancellationToken;

            string? mainKey;
            using (var workspace = new RepoWorkspace(repo.Root, _ => { }))
            {
                await workspace.InitializeAsync(cancellationToken);
                var first = await CheckAsync(workspace, repo.Root, "Lib/Calc.cs", cancellationToken);
                Assert.Contains("mainError", Assert.Single(first.Errors).Error.Message, StringComparison.Ordinal);
                mainKey = BaselineStore.KeyFor(head, workspace.Baseline, workspace.Graph, repo.Root);
            }

            Assert.NotNull(mainKey);
            Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(repo.Root.SharedStateDirectory, "baseline"), "*.json", SearchOption.AllDirectories));

            var log = new ConcurrentQueue<string>();
            using (var workspace = new RepoWorkspace(other, log.Enqueue))
            {
                await workspace.InitializeAsync(cancellationToken);
                var second = await CheckAsync(workspace, other, "Lib/Calc.cs", cancellationToken);
                Assert.Contains("worktreeError", Assert.Single(second.Errors).Error.Message, StringComparison.Ordinal);
                // The key covers the projects a workspace has loaded, so the two keys are compared only once
                // both checks have loaded the same projects. The check above is the compute-once proof: its
                // baseline came from the store the first worktree wrote, as the log below names.
                Assert.Equal(mainKey, BaselineStore.KeyFor(head, workspace.Baseline, workspace.Graph, other));
            }

            Assert.Contains(log, l => l.Contains("stored result(s) read", StringComparison.Ordinal) && !l.Contains("0 stored result(s) read", StringComparison.Ordinal));
        }
        finally
        {
            RemoveWorktree(repo, worktree);
            DeleteDirectory(repo.Root.SharedStateDirectory);
        }
    }

    [Fact]
    public async Task Concurrent_writes_to_one_key_leave_complete_results()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var store = new BaselineStore(repo.Root, _ => { });
        var paths = Enumerable.Range(0, 8).Select(i => repo.PathOf($"f{i}.cs")).ToList();
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, 32),
                new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
                (n, _) =>
                {
                    var path = paths[n % paths.Count];
                    store.WriteFile("key", path, [new CompilerError(path.Relative, 1, 1, "CS0001", "broken")], compilationWide: true);
                    return ValueTask.CompletedTask;
                });

            foreach (var path in paths)
            {
                var stored = store.TryReadFile("key", path);
                Assert.NotNull(stored);
                Assert.Equal("broken", Assert.Single(stored.Value.Errors).Message);
            }
        }
        finally
        {
            DeleteDirectory(repo.Root.SharedStateDirectory);
        }
    }

    [Fact]
    public void One_worktrees_analyzer_copies_survive_another_worktrees_startup()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var worktree = AddWorktree(repo);
        try
        {
            var mainAnalyzer = repo.Full("Gen/bin/Gen.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(mainAnalyzer)!);
            File.Copy(typeof(SharedBaselineTests).Assembly.Location, mainAnalyzer);
            var mainCopy = ShadowCopy(repo.Root, mainAnalyzer);
            var shared = Path.Combine(repo.Root.SharedStateDirectory, "analyzers");
            Assert.StartsWith(shared, mainCopy, StringComparison.OrdinalIgnoreCase);

            var other = RepoRoot.Find(worktree) ?? throw new InvalidOperationException($"no repository at {worktree}");
            _ = new AnalyzerShadow(other);
            Assert.True(File.Exists(mainCopy));

            var worktreeAnalyzer = Path.Combine(other.Path, "Gen", "bin", "Gen.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(worktreeAnalyzer)!);
            File.Copy(typeof(SharedBaselineTests).Assembly.Location, worktreeAnalyzer);
            var worktreeCopy = ShadowCopy(other, worktreeAnalyzer);
            Assert.StartsWith(shared, worktreeCopy, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(mainCopy, worktreeCopy);
        }
        finally
        {
            RemoveWorktree(repo, worktree);
            DeleteDirectory(repo.Root.SharedStateDirectory);
        }
    }

    [Fact]
    public void Recording_a_root_records_the_shared_directory_with_the_common_directory()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        try
        {
            LocalState.RecordRoot(repo.Root);

            Assert.Equal(repo.Root.Path, File.ReadAllText(Path.Combine(repo.Root.StateDirectory, "root")));
            Assert.Equal(repo.Root.GitCommonDirectory, File.ReadAllText(Path.Combine(repo.Root.SharedStateDirectory, "root")));
        }
        finally
        {
            DeleteDirectory(repo.Root.SharedStateDirectory);
        }
    }

    [Fact]
    public void The_shared_state_of_a_repository_that_no_longer_exists_is_removed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fuse-tests", "shared-cleanup-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        try
        {
            var kept = Directory.CreateDirectory(Path.Combine(directory, "0123456789abcdef")).FullName;
            File.WriteAllText(Path.Combine(kept, "engine.log"), "");
            File.WriteAllText(Path.Combine(kept, "root"), directory);
            var gone = Directory.CreateDirectory(Path.Combine(directory, "shared-0123456789abcdef")).FullName;
            File.WriteAllText(Path.Combine(gone, "engine.log"), "");
            File.WriteAllText(Path.Combine(gone, "root"), Path.Combine(directory, "deleted", ".git"));

            Assert.Equal(1, LocalState.RemoveStateOfMissingRepositories(directory, DateTime.UtcNow.AddDays(3)));

            Assert.True(Directory.Exists(kept));
            Assert.False(Directory.Exists(gone));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static Task<CheckResult> CheckAsync(RepoWorkspace workspace, RepoRoot root, string relative, CancellationToken cancellationToken) =>
        new Checker(workspace, new SessionEdits(workspace.Root, null)).CheckAsync(
            new CheckScope.Files([root.PathOf(relative)]), PhaseTimes.None, cancellationToken);

    private static string AssetsFor(string root)
    {
        var escaped = root.Replace("\\", "\\\\", StringComparison.Ordinal);
        return """{"version":3,"targets":{"net10.0":{"Lib/1.0.0":{"type":"project"}}},"project":{"restore":{"projectUniqueName":"<ROOT>/Lib/Lib.csproj","projectPath":"<ROOT>/Lib/Lib.csproj","outputPath":"<ROOT>/Lib/obj/"}}}"""
            .Replace("<ROOT>", escaped, StringComparison.Ordinal);
    }

    private static string ShadowCopy(RepoRoot root, string analyzer)
    {
        var loader = new PathLoader();
        var project = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "P", "P", LanguageNames.CSharp)
            .WithAnalyzerReferences([(AnalyzerReference)new AnalyzerFileReference(analyzer, loader)]);
        using var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution.AddProject(project);
        var shadowed = new AnalyzerShadow(root).Apply(solution);
        return Assert.Single(shadowed.Projects.Single().AnalyzerReferences).FullPath!;
    }

    private static string AddWorktree(FixtureRepo repo)
    {
        var parent = Path.Combine(Path.GetTempPath(), "fuse-tests");
        Directory.CreateDirectory(parent);
        var worktree = Path.Combine(parent, "worktree-" + Guid.NewGuid().ToString("N")[..8]);
        FixtureRepo.Run(repo.Root.Path, "git", "worktree", "add", "--detach", worktree, "HEAD");
        return worktree;
    }

    private static void RemoveWorktree(FixtureRepo repo, string worktree)
    {
        try
        {
            FixtureRepo.Run(repo.Root.Path, "git", "worktree", "remove", "--force", worktree);
        }
        catch (InvalidOperationException)
        {
            DeleteDirectory(worktree);
        }
    }

    private static void DeleteDirectory(string directory)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory))
                    return;
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
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
