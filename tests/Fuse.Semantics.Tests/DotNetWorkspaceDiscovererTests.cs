using Fuse.Semantics;
using Xunit;

namespace Fuse.Semantics.Tests;

// P3.1: workspace discovery order (solution > projects > syntax-only) and ignore rules.
public sealed class DotNetWorkspaceDiscovererTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fuse-discover-tests", Guid.NewGuid().ToString("N"));
    private readonly DotNetWorkspaceDiscoverer _discoverer = new();

    public DotNetWorkspaceDiscovererTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task PrefersSingleSolution()
    {
        Write("App.sln", "");
        Write("src/App/App.csproj", "<Project/>");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.Solution, result.Kind);
        Assert.NotNull(result.SolutionPath);
        Assert.EndsWith("App.sln", result.SolutionPath);
        Assert.Single(result.ProjectPaths);
    }

    [Fact]
    public async Task PrefersSlnOverSlnx()
    {
        Write("App.sln", "");
        Write("App.slnx", "");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.Solution, result.Kind);
        Assert.EndsWith("App.sln", result.SolutionPath);
    }

    [Fact]
    public async Task UsesSlnxWhenNoSln()
    {
        Write("App.slnx", "");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.Solution, result.Kind);
        Assert.EndsWith("App.slnx", result.SolutionPath);
    }

    [Fact]
    public async Task FallsBackToProjectsWhenMultipleSolutions()
    {
        Write("A.sln", "");
        Write("B.sln", "");
        Write("src/App/App.csproj", "<Project/>");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.Projects, result.Kind);
        Assert.Null(result.SolutionPath);
        Assert.Single(result.ProjectPaths);
    }

    [Fact]
    public async Task FallsBackToSyntaxOnlyWhenNoProjectOrSolution()
    {
        Write("src/Loose.cs", "class Loose { }");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.SyntaxOnly, result.Kind);
        Assert.Empty(result.ProjectPaths);
    }

    [Fact]
    public async Task IgnoresBuildDirectories()
    {
        Write("src/App/App.csproj", "<Project/>");
        Write("src/App/bin/Debug/Stale.csproj", "<Project/>");
        Write("obj/Generated.csproj", "<Project/>");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.Projects, result.Kind);
        Assert.Single(result.ProjectPaths);
        Assert.DoesNotContain(result.ProjectPaths, p => p.Contains("bin", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.ProjectPaths, p => p.Contains("obj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IgnoresClaudeWorktrees()
    {
        // Claude Code creates full duplicate checkouts under .claude/worktrees; discovery must not descend into
        // them, or a single-solution repo looks like a multi-solution one and the projects are indexed in copies.
        Write("App.sln", "");
        Write("src/App/App.csproj", "<Project/>");
        Write(".claude/worktrees/wf_1/App.sln", "");
        Write(".claude/worktrees/wf_1/src/App/App.csproj", "<Project/>");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.Solution, result.Kind);
        Assert.EndsWith("App.sln", result.SolutionPath);
        Assert.Single(result.ProjectPaths);
        Assert.DoesNotContain(result.ProjectPaths, p => p.Contains(".claude", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IgnoresNestedRepositoryRoots()
    {
        // A nested checkout anywhere (not just under .claude) is a separate repo; its .git marks it, and its
        // projects must not be discovered even though the directory has no excluded name.
        Write("App.sln", "");
        Write("src/App/App.csproj", "<Project/>");
        Write("external/Lib/.git", "gitdir: /elsewhere");
        Write("external/Lib/Lib.csproj", "<Project/>");
        Write("external/Lib/Lib.sln", "");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.Solution, result.Kind);
        Assert.Single(result.ProjectPaths);
        Assert.DoesNotContain(result.ProjectPaths, p => p.Contains("external", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CollapsesIdenticalSolutionCopies()
    {
        // Two byte-identical .sln copies (a duplicated tree not under a VCS root) must not flip the single-solution
        // decision into projects mode; they collapse to one canonical solution.
        Write("App.sln", "Microsoft Visual Studio Solution File, Format Version 12.00");
        Write("backup/App.sln", "Microsoft Visual Studio Solution File, Format Version 12.00");
        Write("src/App/App.csproj", "<Project/>");

        var result = await _discoverer.DiscoverAsync(_root, CancellationToken.None);

        Assert.Equal(WorkspaceKind.Solution, result.Kind);
        Assert.NotNull(result.SolutionPath);
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of temp test artifacts.
        }
    }
}
