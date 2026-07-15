using Fuse.Cli.Mcp;
using Fuse.Fusion;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

/// <summary>
///     R15: unit tests for the stable operational error prefixes returned by MCP tools and mirrored in CLI commands.
/// </summary>
public sealed class FuseOperationalErrorsTests
{
    [Fact]
    public void FromException_maps_index_rebuilding_exception_to_prefix()
    {
        var message = FuseOperationalErrors.FromException(new IndexRebuildingException("after upgrade to 4.2.0"));
        Assert.StartsWith(FuseOperationalErrors.IndexRebuildingPrefix, message);
        Assert.Contains("after upgrade to 4.2.0", message);
    }

    [Fact]
    public void FromException_maps_index_busy_exception_to_prefix()
    {
        var message = FuseOperationalErrors.FromException(new IndexBusyException());
        Assert.StartsWith(FuseOperationalErrors.IndexBusyPrefix, message);
    }

    [Fact]
    public void FromException_maps_sqlite_busy_to_index_busy_prefix()
    {
        var message = FuseOperationalErrors.FromException(new SqliteException("database is locked", 5));
        Assert.StartsWith(FuseOperationalErrors.IndexBusyPrefix, message);
    }

    [Fact]
    public void FromException_maps_sqlite_locked_to_index_busy_prefix()
    {
        var message = FuseOperationalErrors.FromException(new SqliteException("database table is locked", 6));
        Assert.StartsWith(FuseOperationalErrors.IndexBusyPrefix, message);
    }

    [Fact]
    public void FromException_maps_sharing_violation_io_to_index_busy_prefix()
    {
        var io = new IOException("sharing violation", unchecked((int)0x80070020));
        var message = FuseOperationalErrors.FromException(io);
        Assert.StartsWith(FuseOperationalErrors.IndexBusyPrefix, message);
    }

    [Fact]
    public void FormatIndexNotBuilt_uses_index_not_built_prefix()
    {
        var message = FuseOperationalErrors.FormatIndexNotBuilt("/tmp/.fuse/fuse.db");
        Assert.StartsWith(FuseOperationalErrors.IndexNotBuiltPrefix, message);
        Assert.Contains("/tmp/.fuse/fuse.db", message);
    }

    [Fact]
    public void FormatWorkspaceNotFound_uses_workspace_not_found_prefix()
    {
        var message = FuseOperationalErrors.FormatWorkspaceNotFound("/missing/root");
        Assert.StartsWith(FuseOperationalErrors.WorkspaceNotFoundPrefix, message);
        Assert.Contains("/missing/root", message);
    }

    [Fact]
    public void FromException_maps_fusion_validation_to_validation_error_prefix()
    {
        var message = FuseOperationalErrors.FromException(new FusionValidationException(["seed not found"]));
        Assert.StartsWith(FuseOperationalErrors.ValidationErrorPrefix, message);
        Assert.Contains("seed not found", message);
    }

    [Fact]
    public void FromException_maps_unknown_to_internal_error_prefix()
    {
        var message = FuseOperationalErrors.FromException(new InvalidOperationException("unexpected"));
        Assert.StartsWith(FuseOperationalErrors.InternalErrorPrefix, message);
        Assert.Contains("unexpected", message);
    }

    [Fact]
    public void FromException_maps_cancellation_to_internal_error_prefix()
    {
        var message = FuseOperationalErrors.FromException(new OperationCanceledException());
        Assert.StartsWith(FuseOperationalErrors.InternalErrorPrefix, message);
    }

    [Fact]
    public void FromException_maps_change_source_to_validation_error_prefix()
    {
        // A non-git workspace (or a bad base ref) is an expected precondition miss for fuse_review, not an
        // internal failure; it must surface as validation_error, never internal_error.
        var message = FuseOperationalErrors.FromException(
            new Fuse.Retrieval.ChangeSourceException("Source directory is not a git repository."));
        Assert.StartsWith(FuseOperationalErrors.ValidationErrorPrefix, message);
        Assert.Contains("not a git repository", message);
    }

    [Fact]
    public void FromException_maps_change_detection_to_validation_error_prefix()
    {
        var message = FuseOperationalErrors.FromException(
            new Fuse.Fusion.Scoping.ChangeDetectionException("Git is not available on PATH."));
        Assert.StartsWith(FuseOperationalErrors.ValidationErrorPrefix, message);
    }

    [Fact]
    public async Task ExecuteMcpAsync_never_throws_on_operational_failure()
    {
        var result = await FuseOperationalErrors.ExecuteMcpAsync(() =>
            throw new SqliteException("database is locked", 5));
        Assert.StartsWith(FuseOperationalErrors.IndexBusyPrefix, result);
    }
}
