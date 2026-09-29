using System.Text;
using Fuse.Paths;

namespace Fuse.Harnesses;

/// <summary>
///     Keeps the <c>.fuse</c> folder out of git. The folder is reserved for files Fuse may keep inside a repository, such
///     as an index, and Fuse 4 kept its index there, so a folder left by Fuse 4 or written by a later version is never
///     committed. <c>fuse init</c> adds the entry to the repository's root <c>.gitignore</c> and leaves the rest of the
///     file as it was.
/// </summary>
internal static class GitIgnore
{
    /// <summary>The entry <c>fuse init</c> adds.</summary>
    internal const string FuseFolder = ".fuse/";

    /// <summary>
    ///     The lines that already decide about the folder, ignoring it or, with <c>!</c>, keeping it. A file with one of them
    ///     is not changed, so a repository that chose to commit the folder keeps that choice.
    /// </summary>
    private static readonly HashSet<string> Decided = [".fuse", ".fuse/", "/.fuse", "/.fuse/", "**/.fuse", "**/.fuse/"];

    /// <summary>
    ///     Adds <see cref="FuseFolder"/> to the root <c>.gitignore</c>, creating the file when there is none, and returns its
    ///     repository-relative path; returns null when the file already has a line about the folder.
    /// </summary>
    /// <exception cref="IOException">The file could not be read or written. The message names the file.</exception>
    public static string? AddFuseFolder(RepoRoot root)
    {
        var path = Path.Combine(root.Path, ".gitignore");
        string text;
        try
        {
            // Decoded without dropping a byte order mark, so the file is written back with the one it had.
            text = File.Exists(path) ? Encoding.UTF8.GetString(File.ReadAllBytes(path)) : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"could not read .gitignore ({e.Message})", e);
        }

        var lines = text.Split('\n').Select(l => l.Trim().TrimStart('!'));
        if (lines.Any(Decided.Contains))
            return null;

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : text.Length > 0 ? "\n" : Environment.NewLine;
        var separator = text.Length == 0 || text.EndsWith('\n') ? "" : newline;
        return SettingsFile.WriteText(root, path, text + separator + FuseFolder + newline);
    }
}
