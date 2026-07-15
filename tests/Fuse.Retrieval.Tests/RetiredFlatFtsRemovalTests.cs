using System.Runtime.CompilerServices;
using Xunit;

namespace Fuse.Retrieval.Tests;

// R3: the retired flat per-source FTS generator and FUSE_FLAT_FTS diagnostic flag were deleted; this test
// fails if either identifier reappears under src/ or site/.
public sealed class RetiredFlatFtsRemovalTests
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".next", ".source", "bin", "node_modules", "obj",
    };

    private static readonly string[] ForbiddenTokens =
    [
        "FtsCandidateGenerator",
        "FUSE_FLAT_FTS",
        "RetrievalDiagnosticFlags",
        "EnableFlatFts",
    ];

    private static string? RepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fuse.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    [Theory]
    [InlineData("src")]
    [InlineData("site")]
    public void RetiredFlatFtsIdentifiersDoNotReappear(string scanRoot)
    {
        var root = RepoRoot();
        Assert.NotNull(root);
        var directory = Path.Combine(root!, scanRoot);
        Assert.True(Directory.Exists(directory), $"scan root not found at {directory}");

        var violations = new List<string>();
        foreach (var file in EnumerateTrackedFiles(directory))
        {
            if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = File.ReadAllText(file);
            foreach (var token in ForbiddenTokens)
            {
                if (text.Contains(token, StringComparison.Ordinal))
                    violations.Add($"{file}: contains '{token}'");
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    private static IEnumerable<string> EnumerateTrackedFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                if (!ExcludedDirectories.Contains(Path.GetFileName(directory)))
                    pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(current))
                yield return file;
        }
    }
}
