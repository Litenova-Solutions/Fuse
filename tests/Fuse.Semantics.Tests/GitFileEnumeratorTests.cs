using System.Diagnostics;
using Fuse.Semantics;
using Xunit;

namespace Fuse.Semantics.Tests;

// Git-native enumeration: a git work tree is listed by git (tracked plus untracked-not-ignored, excluding ignored
// files and nested worktrees); a non-git directory returns null so the caller falls back to the directory walk.
public sealed class GitFileEnumeratorTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fuse-gitenum-tests", Guid.NewGuid().ToString("N"));
    private readonly GitFileEnumerator _enumerator = new();

    public GitFileEnumeratorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ReturnsNullForNonGitDirectory()
    {
        File.WriteAllText(Path.Combine(_root, "Loose.cs"), "class Loose { }");

        var result = await _enumerator.TryListAsync(_root, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ListsTrackedAndUntrackedButNotIgnoredOrNestedWorktree()
    {
        if (!TryInitGitRepo())
            return; // git not available in this environment; the fallback path is covered by the scanner tests.

        File.WriteAllText(Path.Combine(_root, ".gitignore"), "ignored.cs\n");
        File.WriteAllText(Path.Combine(_root, "Tracked.cs"), "class Tracked { }");
        File.WriteAllText(Path.Combine(_root, "Untracked.cs"), "class Untracked { }");
        File.WriteAllText(Path.Combine(_root, "ignored.cs"), "class Ignored { }");
        RunGit("add", "Tracked.cs", ".gitignore");

        // A real embedded repository (its own .git) must not be recursed by the outer repo's listing.
        if (!RunGit("init", "nested"))
            return; // nested init unsupported here; the fallback and filter paths are covered elsewhere.
        File.WriteAllText(Path.Combine(_root, "nested", "Nested.cs"), "class Nested { }");

        var result = await _enumerator.TryListAsync(_root, CancellationToken.None);

        Assert.NotNull(result);
        var names = result!.Select(p => p.Replace('\\', '/')).ToList();
        Assert.Contains(names, p => p.EndsWith("Tracked.cs", StringComparison.Ordinal));
        Assert.Contains(names, p => p.EndsWith("Untracked.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(names, p => p.EndsWith("ignored.cs", StringComparison.Ordinal));
        // git reports an embedded repository as a single "nested/" directory marker and does not recurse it, so
        // its files never appear; the pipeline then drops the directory marker via its existence check.
        Assert.DoesNotContain(names, p => p.EndsWith("Nested.cs", StringComparison.Ordinal));
    }

    private bool TryInitGitRepo()
    {
        try
        {
            return RunGit("init") && RunGit("config", "user.email", "t@e.st") && RunGit("config", "user.name", "t");
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool RunGit(params string[] args)
    {
        var startInfo = new ProcessStartInfo { FileName = "git", WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        using var process = Process.Start(startInfo);
        if (process is null)
            return false;
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; git pack files can linger briefly on Windows.
        }
    }
}
