using System.Runtime.CompilerServices;
using Xunit;

namespace Fuse.Retrieval.Tests;

// R3: the retired flat per-source FTS generator and FUSE_FLAT_FTS diagnostic flag were deleted; this test
// fails if either identifier reappears under src/ or site/.
public sealed class RetiredFlatFtsRemovalTests
{
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
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
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
}
