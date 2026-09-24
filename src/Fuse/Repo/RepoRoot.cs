using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Fuse.Repo;

/// <summary>
///     The canonical root of a git repository: the nearest ancestor holding <c>.git</c>, resolved through
///     junctions, symlinks, <c>subst</c> drives and 8.3 short names so every spelling of a path maps to one engine.
/// </summary>
internal sealed class RepoRoot
{
    private RepoRoot(string path) => Path = path;

    /// <summary>Absolute canonical path without a trailing separator.</summary>
    public string Path { get; }

    /// <summary>Name of the engine's pipe for this root. Case-insensitive on Windows, where paths are.</summary>
    public string PipeName => "fuse-" + Hash(OperatingSystem.IsWindows() ? Path.ToUpperInvariant() : Path);

    /// <summary>Directory for Fuse's own files (log, shadow test output). Inside <c>obj</c>, which .NET repositories ignore.</summary>
    public string StateDirectory => System.IO.Path.Combine(Path, "obj", "fuse");

    /// <summary>Finds the repository containing <paramref name="start"/>, or null when there is none.</summary>
    public static RepoRoot? Find(string start)
    {
        var dir = new DirectoryInfo(Canonicalize(System.IO.Path.GetFullPath(start)));
        if (!dir.Exists && dir.Parent is not null)
            dir = dir.Parent;
        for (var d = dir; d is not null; d = d.Parent)
        {
            var git = System.IO.Path.Combine(d.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return new RepoRoot(d.FullName.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        }

        return null;
    }

    /// <summary>Returns the repository-relative form of an absolute path, with forward slashes.</summary>
    public string Relative(string absolute)
    {
        var full = System.IO.Path.GetFullPath(absolute);
        var rel = System.IO.Path.GetRelativePath(Path, full);
        if (rel.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(rel))
        {
            // The caller may have used a different spelling of the root (a junction or short name).
            var canonical = Canonicalize(full);
            rel = System.IO.Path.GetRelativePath(Path, canonical);
        }

        return rel.Replace('\\', '/');
    }

    /// <summary>Returns the absolute form of a path given relative to the root or absolute, canonicalized.</summary>
    public string Absolute(string path)
    {
        var full = System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(Path, path);
        full = System.IO.Path.GetFullPath(full);
        if (full.StartsWith(Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return full;
        var canonical = Canonicalize(full);
        return canonical;
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    /// <summary>Resolves a path to its final form. Returns the input when the path does not exist.</summary>
    internal static string Canonicalize(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsFinalPath(path) ?? path;
            var resolved = UnixRealPath(path);
            return resolved ?? path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            return path;
        }
    }

    private static string? WindowsFinalPath(string path)
    {
        const uint fileReadAttributes = 0x80;
        const uint shareAll = 0x7;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        using var handle = Native.CreateFileW(path, fileReadAttributes, shareAll, 0, openExisting, backupSemantics, 0);
        if (handle.IsInvalid)
            return null;
        var buffer = new char[1024];
        var length = Native.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
            return null;
        var result = new string(buffer, 0, (int)length);
        if (result.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            return @"\\" + result[8..];
        if (result.StartsWith(@"\\?\", StringComparison.Ordinal))
            return result[4..];
        return result;
    }

    private static string? UnixRealPath(string path)
    {
        var ptr = Native.realpath(path, 0);
        if (ptr == 0)
            return null;
        try
        {
            return Marshal.PtrToStringUTF8(ptr);
        }
        finally
        {
            Native.free(ptr);
        }
    }

    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
            string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetFinalPathNameByHandleW(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle, [Out] char[] buffer, uint length, uint flags);

        [DllImport("libc", EntryPoint = "realpath")]
#pragma warning disable CA2101, SYSLIB1054 // UTF-8 marshalling is what libc expects.
        public static extern nint realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint resolved);
#pragma warning restore CA2101, SYSLIB1054

        [DllImport("libc", EntryPoint = "free")]
        public static extern void free(nint ptr);
    }
}
