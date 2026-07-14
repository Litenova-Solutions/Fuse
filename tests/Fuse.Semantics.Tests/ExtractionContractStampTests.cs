using System.Runtime.CompilerServices;
using Xunit;

namespace Fuse.Semantics.Tests;

// R22: the extraction-contract version must be read by the indexer, so a bump is visible in review. This guard
// fails if SemanticIndexer stops referencing WorkspaceIndexSchema.ExtractionContractVersion (which would let an
// extractor change ship without a rebuild-forcing stamp).
public sealed class ExtractionContractStampTests
{
    private static string? RepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fuse.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    [Fact]
    public void SemanticIndexer_StampsExtractionContractVersion()
    {
        var root = RepoRoot();
        Assert.NotNull(root);
        var indexerPath = Path.Combine(root!, "src", "Core", "Fuse.Semantics", "SemanticIndexer.cs");
        Assert.True(File.Exists(indexerPath), $"SemanticIndexer.cs not found at {indexerPath}");

        var text = File.ReadAllText(indexerPath);
        Assert.Contains("ExtractionVersionMetaKey", text, StringComparison.Ordinal);
        Assert.Contains("ExtractionContractVersion", text, StringComparison.Ordinal);
    }
}
