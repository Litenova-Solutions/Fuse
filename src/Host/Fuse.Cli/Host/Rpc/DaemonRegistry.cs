using System.Diagnostics;
using System.Text.Json;
using Fuse.Cli.Serialization;
using Fuse.Reduction.Caching;

namespace Fuse.Cli.Rpc;

/// <summary>
///     A machine-visible registry of running <c>fuse host</c> daemons (R28). Each daemon writes a small descriptor
///     (served root, process id, version, start time) under <c>{user-data}/daemons/</c> on start and removes it on
///     shutdown, so <c>fuse_workspace action=doctor</c> can list every running daemon with its served root and
///     version without cross-process command-line introspection. <see cref="List" /> prunes descriptors whose
///     process is no longer alive, so a crashed daemon's stale entry is cleaned up on the next read.
/// </summary>
public static class DaemonRegistry
{
    private static string RegistryDirectory() =>
        Path.Combine(FuseStorePaths.GetUserDataDirectory(), "daemons");

    private static string DescriptorPath(string root) =>
        Path.Combine(RegistryDirectory(), HostEndpoint.PipeName(root) + ".json");

    /// <summary>
    ///     Records this process as the daemon serving <paramref name="root" />. Overwrites any prior descriptor for
    ///     the same root, so a new-version daemon replaces a stale entry. Best-effort: an IO failure is swallowed so
    ///     the daemon still serves.
    /// </summary>
    /// <param name="root">The absolute served root.</param>
    /// <param name="version">The running daemon version.</param>
    /// <param name="startedUtc">The ISO-8601 UTC start time.</param>
    public static void Register(string root, string version, string startedUtc)
    {
        try
        {
            Directory.CreateDirectory(RegistryDirectory());
            var descriptor = new DaemonDescriptor(Path.GetFullPath(root), Environment.ProcessId, version, startedUtc);
            var json = JsonSerializer.Serialize(descriptor, FuseCliJsonContext.Default.DaemonDescriptor);
            File.WriteAllText(DescriptorPath(root), json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Removes this root's daemon descriptor on shutdown. Best-effort.</summary>
    /// <param name="root">The absolute served root.</param>
    public static void Deregister(string root)
    {
        try
        {
            var path = DescriptorPath(root);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    ///     Lists the running daemons, pruning (and deleting) descriptors whose process is no longer alive.
    /// </summary>
    /// <returns>The live daemon descriptors, ordered by served root.</returns>
    public static IReadOnlyList<DaemonDescriptor> List()
    {
        var directory = RegistryDirectory();
        if (!Directory.Exists(directory))
            return [];

        var live = new List<DaemonDescriptor>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            DaemonDescriptor? descriptor = null;
            try
            {
                descriptor = JsonSerializer.Deserialize(File.ReadAllText(file), FuseCliJsonContext.Default.DaemonDescriptor);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }

            if (descriptor is null || !IsProcessAlive(descriptor.ProcessId))
            {
                TryDelete(file);
                continue;
            }

            live.Add(descriptor);
        }

        return live.OrderBy(d => d.Root, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Whether a process id is currently running. Checks existence only (no command-line read), so it works where
    // cross-process introspection is denied. A reused pid can false-positive, which only delays a stale-entry prune.
    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
///     A running <c>fuse host</c> daemon's public identity (R28): the root it serves, its process id, its version,
///     and when it started. Surfaced by <c>fuse_workspace action=doctor</c> so a stale or mismatched daemon is
///     visible rather than an invisible accumulation of orphaned servers.
/// </summary>
/// <param name="Root">The absolute served workspace root.</param>
/// <param name="ProcessId">The daemon's process id.</param>
/// <param name="Version">The daemon's Fuse version.</param>
/// <param name="StartedUtc">The ISO-8601 UTC start time.</param>
public sealed record DaemonDescriptor(string Root, int ProcessId, string Version, string StartedUtc);
