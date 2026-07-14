using Fuse.Cli.Mcp;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

// U1 / Decision D2: fuse_workspace action=apply is the server's one explicit tree-write path. It must be a dry run
// unless write=true, must actually write when asked, and must refuse a path that escapes the workspace root. The
// apply action does not use the indexer, so these tests drive FuseWorkspaceAsync directly over a temp directory.
public sealed class WorkspaceApplyTests
{
    private static string TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuse-apply-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Dry_run_reports_without_writing()
    {
        var root = TempRoot();
        try
        {
            var result = await FuseTools.FuseWorkspaceAsync(null!, "apply", root, file: "src/A.cs", content: "class A {}");
            Assert.Contains("dry run", result);
            Assert.Contains("would create", result);
            Assert.False(File.Exists(Path.Combine(root, "src", "A.cs")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Write_creates_the_file_with_the_content()
    {
        var root = TempRoot();
        try
        {
            var result = await FuseTools.FuseWorkspaceAsync(null!, "apply", root, file: "src/A.cs", content: "class A {}", write: true);
            Assert.Contains("applied", result);
            var written = Path.Combine(root, "src", "A.cs");
            Assert.True(File.Exists(written));
            Assert.Equal("class A {}", await File.ReadAllTextAsync(written));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task A_path_escaping_the_root_is_refused()
    {
        var root = TempRoot();
        var outside = Path.Combine(Path.GetDirectoryName(root)!, "escaped.txt");
        try
        {
            var result = await FuseTools.FuseWorkspaceAsync(null!, "apply", root, file: "../escaped.txt", content: "x", write: true);
            Assert.Contains("refusing to write", result);
            Assert.Contains("outside the workspace root", result);
            Assert.False(File.Exists(outside));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            if (File.Exists(outside)) File.Delete(outside);
        }
    }

    [Fact]
    public async Task Apply_without_a_file_errors()
    {
        var root = TempRoot();
        try
        {
            var result = await FuseTools.FuseWorkspaceAsync(null!, "apply", root, file: "", content: "x", write: true);
            // R15 operational-error taxonomy: a missing file argument is a validation_error, not a bare "Error".
            Assert.StartsWith("validation_error:", result);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
