using System.Runtime.CompilerServices;
using Fuse.Semantics;
using Xunit;

namespace Fuse.Semantics.Tests;

// R41: the git co-change collector (a large share of the index hot path) does not run on the default index
// path, because the co-change prior is default-off (D6). It is gated behind FUSE_COCHANGE, default off.
public sealed class GitCoChangeGateTests
{
    [Fact]
    public void IsCollectionEnabled_DefaultsOff_AndOptsIn()
    {
        var original = Environment.GetEnvironmentVariable(GitCoChangeCollector.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(GitCoChangeCollector.EnvVar, null);
            Assert.False(GitCoChangeCollector.IsCollectionEnabled());
            Environment.SetEnvironmentVariable(GitCoChangeCollector.EnvVar, "1");
            Assert.True(GitCoChangeCollector.IsCollectionEnabled());
            Environment.SetEnvironmentVariable(GitCoChangeCollector.EnvVar, "off");
            Assert.False(GitCoChangeCollector.IsCollectionEnabled());
        }
        finally
        {
            Environment.SetEnvironmentVariable(GitCoChangeCollector.EnvVar, original);
        }
    }

    [Fact]
    public void SemanticIndexer_GatesTheCollectorOnTheFlag()
    {
        // Guard: the indexer must gate the co-change collector on IsCollectionEnabled, so a forgotten guard (the
        // collector running unconditionally on every index) is caught in review.
        var root = RepoRoot();
        Assert.NotNull(root);
        var indexerPath = Path.Combine(root!, "src", "Core", "Fuse.Semantics", "SemanticIndexer.cs");
        var text = File.ReadAllText(indexerPath);

        Assert.Contains("GitCoChangeCollector.IsCollectionEnabled()", text, StringComparison.Ordinal);
        // The two collector calls must be inside the gate: the guard token appears before each call.
        var guardCount = CountOccurrences(text, "GitCoChangeCollector.IsCollectionEnabled()");
        var callCount = CountOccurrences(text, "_coChangeCollector.CollectAndStoreAsync");
        Assert.True(guardCount >= callCount, "each CollectAndStoreAsync call must be gated by IsCollectionEnabled");
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static string? RepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fuse.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }
}
