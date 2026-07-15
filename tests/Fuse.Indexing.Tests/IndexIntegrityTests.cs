using Fuse.Indexing;
using Xunit;

namespace Fuse.Indexing.Tests;

// R31: a store failing its invariants is never healthy, so it is never served as ready. These cover each cheap
// state-based invariant the read paths check on open and status.
public sealed class IndexIntegrityTests
{
    [Fact]
    public void HealthyPopulatedStore_Passes()
    {
        var state = new WorkspaceIndexState(
            SchemaVersion: WorkspaceIndexSchema.TargetVersion, WorkspaceIndexStatus.Warm,
            FileCount: 10, SymbolCount: 40, Mode: "semantic", FtsAvailable: true, ChunkCount: 60);

        Assert.True(IndexIntegrity.Check(state).Healthy);
    }

    [Fact]
    public void UnknownMode_IsViolation()
    {
        var unknown = new WorkspaceIndexState(WorkspaceIndexSchema.TargetVersion, WorkspaceIndexStatus.Warm, 10, 40, "unknown", true, 60);
        var missing = new WorkspaceIndexState(WorkspaceIndexSchema.TargetVersion, WorkspaceIndexStatus.Warm, 10, 40, null, true, 60);

        Assert.False(IndexIntegrity.Check(unknown).Healthy);
        Assert.False(IndexIntegrity.Check(missing).Healthy);
        Assert.Contains(IndexIntegrity.Check(unknown).Violations, v => v.Contains("mode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SymbolsWithZeroChunks_OnFtsAvailable_IsViolation()
    {
        var state = new WorkspaceIndexState(WorkspaceIndexSchema.TargetVersion, WorkspaceIndexStatus.Warm, 10, 40, "semantic", FtsAvailable: true, ChunkCount: 0);

        var result = IndexIntegrity.Check(state);
        Assert.False(result.Healthy);
        Assert.Contains(result.Violations, v => v.Contains("chunks", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SymbolsWithZeroChunks_WhenFtsUnavailable_IsHealthy()
    {
        // An FTS-unavailable runtime legitimately has no chunk index; zero chunks there is not a violation.
        var state = new WorkspaceIndexState(WorkspaceIndexSchema.TargetVersion, WorkspaceIndexStatus.Warm, 10, 40, "semantic", FtsAvailable: false, ChunkCount: 0);

        Assert.True(IndexIntegrity.Check(state).Healthy);
    }

    [Fact]
    public void MissingSchemaVersion_IsViolation()
    {
        var state = new WorkspaceIndexState(SchemaVersion: 0, WorkspaceIndexStatus.Warm, 10, 40, "semantic", true, 60);

        Assert.False(IndexIntegrity.Check(state).Healthy);
    }

    [Fact]
    public void EmptyStore_IsHealthy_NotAViolation()
    {
        // A store with no files is cold/not-indexed (reported not_indexed), not internally inconsistent.
        var state = new WorkspaceIndexState(WorkspaceIndexSchema.TargetVersion, WorkspaceIndexStatus.Cold, FileCount: 0, SymbolCount: 0, Mode: null, FtsAvailable: true, ChunkCount: 0);

        Assert.True(IndexIntegrity.Check(state).Healthy);
    }
}
