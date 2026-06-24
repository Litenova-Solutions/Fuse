using System.Security.Cryptography;
using System.Text;

namespace Fuse.Cli.Rpc;

/// <summary>
///     Derives the UI transport address from a repository root, so the extension and the host agree on where to
///     connect without passing the address out of band. The address is a stable function of the root, which lets
///     a second VS Code window for the same repository find the already-running host, and keeps two different
///     repositories on distinct endpoints.
/// </summary>
public static class HostEndpoint
{
    /// <summary>
    ///     The Windows named-pipe name for a repository root (the bare name, without the <c>\\.\pipe\</c> prefix
    ///     that <see cref="System.IO.Pipes.NamedPipeServerStream" /> adds).
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root path.</param>
    /// <returns>A stable pipe name such as <c>fuse-host-1a2b3c4d5e6f7a8b</c>.</returns>
    public static string PipeName(string repositoryRoot) => "fuse-host-" + RootHash(repositoryRoot);

    /// <summary>
    ///     The Unix domain socket path for a repository root, under the system temp directory.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root path.</param>
    /// <returns>A stable socket path such as <c>{temp}/fuse-host-1a2b3c4d5e6f7a8b.sock</c>.</returns>
    public static string SocketPath(string repositoryRoot) =>
        Path.Combine(Path.GetTempPath(), "fuse-host-" + RootHash(repositoryRoot) + ".sock");

    // A short, stable hex hash of the normalized root. SHA-256 truncated to 8 bytes is collision-safe enough for
    // distinguishing repository roots on one machine and keeps the pipe and socket names short.
    private static string RootHash(string repositoryRoot)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot))
            .ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }
}
