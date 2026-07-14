using Fuse.Cli.Rpc;
using Fuse.Reduction.Caching;
using Xunit;

namespace Fuse.Cli.Tests.Host;

// R28: running fuse host daemons are visible in a machine registry (served root + version), and a crashed
// daemon's stale descriptor is pruned, so accumulation or a mismatched version is visible in doctor.
public sealed class DaemonRegistryTests
{
    [Fact]
    public void RegisterThenList_ShowsRootAndVersion()
    {
        var root = NewRoot();
        try
        {
            DaemonRegistry.Register(root, "4.2.0", DateTimeOffset.UtcNow.ToString("O"));

            var entry = Assert.Single(DaemonRegistry.List(), d => SameRoot(d.Root, root));
            Assert.Equal("4.2.0", entry.Version);
            Assert.Equal(Environment.ProcessId, entry.ProcessId);
        }
        finally
        {
            DaemonRegistry.Deregister(root);
        }
    }

    [Fact]
    public void Deregister_RemovesTheDescriptor()
    {
        var root = NewRoot();
        DaemonRegistry.Register(root, "4.2.0", DateTimeOffset.UtcNow.ToString("O"));
        DaemonRegistry.Deregister(root);

        Assert.DoesNotContain(DaemonRegistry.List(), d => SameRoot(d.Root, root));
    }

    [Fact]
    public void ReRegister_SameRoot_OverwritesWithNewVersion()
    {
        var root = NewRoot();
        try
        {
            DaemonRegistry.Register(root, "4.1.0", DateTimeOffset.UtcNow.ToString("O"));
            DaemonRegistry.Register(root, "4.2.0", DateTimeOffset.UtcNow.ToString("O"));

            var entries = DaemonRegistry.List().Where(d => SameRoot(d.Root, root)).ToList();
            var entry = Assert.Single(entries); // one descriptor per root, not two.
            Assert.Equal("4.2.0", entry.Version);
        }
        finally
        {
            DaemonRegistry.Deregister(root);
        }
    }

    [Fact]
    public void List_PrunesDescriptorWhoseProcessIsDead()
    {
        // Write a descriptor directly for a dead process id (this process's start time makes reuse of a huge id
        // effectively impossible); List must prune it and delete the file.
        var root = NewRoot();
        var deadPid = 2_000_000_000;
        var descriptor = new DaemonDescriptor(Path.GetFullPath(root), deadPid, "4.2.0", DateTimeOffset.UtcNow.ToString("O"));
        var dir = Path.Combine(FuseStorePaths.GetUserDataDirectory(), "daemons");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, HostEndpoint.PipeName(root) + ".json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(descriptor));

        Assert.DoesNotContain(DaemonRegistry.List(), d => SameRoot(d.Root, root));
        Assert.False(File.Exists(file)); // the stale descriptor was cleaned up.
    }

    private static bool SameRoot(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "fuse-daemon-reg", Guid.NewGuid().ToString("N"));
}
