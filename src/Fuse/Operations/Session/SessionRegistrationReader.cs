using System.Diagnostics;
using System.Globalization;
using Fuse.Identity;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
namespace Fuse.Operations.Session;

/// <summary>
///     Reads the live session registrations from the registrations file in the state directory, as the engine reads it:
///     one line per registration with the expiry ticks, the name, the patterns and a trailing <c>pid:</c> field. This
///     layer may not use the check layer that owns the file, so it reads the documented line format here instead.
/// </summary>
internal static class SessionRegistrationReader
{
    /// <summary>The most registrations that are live at once, as the engine holds.</summary>
    private const int MaxLiveRegistrations = 64;

    /// <summary>The most patterns one registration holds, as the engine holds.</summary>
    private const int MaxPatternsPerRegistration = 32;

    /// <summary>The file the registrations are kept in, as the engine keeps it.</summary>
    internal static string FilePath(RepoRoot root) => Path.Combine(root.StateDirectory, "registered-sessions.tsv");

    /// <summary>
    ///     The registrations that have not ended as of <paramref name="now"/>, named without the stored prefix, leaving
    ///     out those that have: expired ones, ones tied to a process that has exited, and lines that do not read back.
    ///     The file is only read: the engine is its one writer, and it prunes the file on its own writes.
    /// </summary>
    internal static IReadOnlyList<ReportedSession> ReadLive(RepoRoot root, DateTimeOffset now)
    {
        var path = FilePath(root);
        string[] lines;
        try
        {
            if (!File.Exists(path))
                return [];
            lines = File.ReadAllLines(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }

        var order = new List<string>();
        var byName = new Dictionary<string, ReportedSession>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var live = ParseLine(line, now);
            if (live is null)
                continue;

            if (byName.ContainsKey(live.Name))
                order.Remove(live.Name);
            byName[live.Name] = live;
            order.Add(live.Name);
            if (order.Count > MaxLiveRegistrations)
            {
                byName.Remove(order[0]);
                order.RemoveAt(0);
            }
        }

        return [.. order.Select(name => byName[name])];
    }

    /// <summary>The registration <paramref name="line"/> names when it is still live as of <paramref name="now"/>, or null when it has ended or does not read back.</summary>
    private static ReportedSession? ParseLine(string line, DateTimeOffset now)
    {
        var parts = line.Split('\t');
        if (parts.Length < 3 || !long.TryParse(parts[0], out var ticks) || ticks < 0 || ticks > 3155378975999999999L || !SessionId.IsValidRegistrationName(parts[1]))
            return null;
        int? pid = null;
        var named = parts[2..];
        if (named.Length > 0 && named[^1].StartsWith("pid:", StringComparison.Ordinal))
        {
            var text = named[^1]["pid:".Length..];
            named = named[..^1];
            pid = int.TryParse(text, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : -1;
        }

        var patterns = new List<string>(named.Length);
        foreach (var pattern in named)
        {
            if (!PathPattern.Normalize(pattern, out var clean))
                return null;
            patterns.Add(clean);
        }

        if (pid is -1 || patterns.Count == 0 || patterns.Count > MaxPatternsPerRegistration)
            return null;
        var expires = new DateTimeOffset(ticks, TimeSpan.Zero);
        if (expires <= now || (pid is { } tied && !IsRunning(tied)))
            return null;
        return new ReportedSession(parts[1], [.. patterns], pid, expires);
    }

    /// <summary>Whether the process <paramref name="pid"/> names is running. A process that has no entry, or one whose state cannot be read, has exited.</summary>
    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
