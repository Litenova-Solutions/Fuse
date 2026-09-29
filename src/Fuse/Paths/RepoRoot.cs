using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Fuse.Paths;

/// <summary>
///     The canonical root of a git repository: the nearest ancestor holding <c>.git</c>, resolved through
///     junctions, symlinks, <c>subst</c> drives and 8.3 short names so every spelling of a path maps to one engine. Every
///     <see cref="RepoPath"/> comes from it (<see cref="PathOf"/>), so one file has one value however it is spelled.
/// </summary>
internal sealed class RepoRoot
{
    private RepoRoot(string path) => Path = path;

    /// <summary>Absolute canonical path without a trailing separator.</summary>
    public string Path { get; }

    /// <summary>Name of the engine's pipe for this root. Case-insensitive on Windows, where paths are.</summary>
    public string PipeName => "fuse-" + Hash(OperatingSystem.IsWindows() ? Path.ToUpperInvariant() : Path);

    /// <summary>
    ///     Directory for Fuse's own files for this repository (engine log, hook log, shadow test output, test results).
    ///     It lives in the user's local application data, so Fuse never writes inside the repository.
    /// </summary>
    public string StateDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "fuse",
        "repos",
        PipeName["fuse-".Length..]);

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
    public string Relative(string absolute) => PathOf(absolute).Relative;

    /// <summary>Returns the absolute form of a path given relative to the root or absolute; a path outside the root's spelling is canonicalized.</summary>
    public string Absolute(string path) => PathOf(path).Absolute;

    /// <summary>The repository path <paramref name="path"/> names, given absolute or relative to the root.</summary>
    /// <remarks>
    ///     A path spelled under the root is only made absolute and normalized (separators, <c>.</c> and <c>..</c>
    ///     segments, and 8.3 short names, which <see cref="System.IO.Path.GetFullPath(string)"/> expands). Any other
    ///     spelling is canonicalized, so a path given through a junction, a symlink or a <c>subst</c> drive of the root is
    ///     the same value as the path spelled under the root. That costs a file system call, which the common case, a
    ///     path spelled under the root, does not pay.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or not a valid path, for example because it holds a NUL character.</exception>
    public RepoPath PathOf(string path)
    {
        var full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(Path, path));
        return new RepoPath(this, Contains(full) ? full : Canonicalize(full));
    }

    /// <summary>
    ///     True when <paramref name="absolute"/> is the root or a path under it in the root's own spelling, compared the
    ///     way the file system does. It canonicalizes nothing, so it costs no file system call, and a path under another
    ///     spelling of the root is not contained.
    /// </summary>
    public bool Contains(string absolute) =>
        absolute.StartsWith(Path, RepoPath.Comparison)
        && (absolute.Length == Path.Length || absolute[Path.Length] == System.IO.Path.DirectorySeparatorChar || absolute[Path.Length] == System.IO.Path.AltDirectorySeparatorChar);

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
