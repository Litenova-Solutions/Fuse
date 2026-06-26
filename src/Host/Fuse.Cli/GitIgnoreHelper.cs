using Fuse.Collection.FileSystem;

namespace Fuse.Cli;

/// <summary>
///     Helpers for updating repository <c>.gitignore</c> files.
/// </summary>
public static class GitIgnoreHelper
{
    /// <summary>Gitignore pattern for the Fuse store directory.</summary>
    public const string FuseIgnoreEntry = ".fuse/";

    /// <summary>
    ///     Appends <see cref="FuseIgnoreEntry" /> to the repository <c>.gitignore</c> when no equivalent entry exists.
    /// </summary>
    /// <param name="startDirectory">Directory used to resolve the git repository root.</param>
    /// <param name="writeStep">Called with a status message when the file is created or updated.</param>
    /// <param name="writeNote">Called with a note when the file could not be updated.</param>
    public static void TryEnsureFuseEntry(string startDirectory, Action<string> writeStep, Action<string> writeNote)
    {
        var repoRoot = RepositoryRootResolver.TryFindRepositoryRoot(startDirectory) ?? startDirectory;
        var gitIgnorePath = Path.Combine(repoRoot, ".gitignore");

        try
        {
            if (File.Exists(gitIgnorePath))
            {
                var content = File.ReadAllText(gitIgnorePath);
                if (ContentHasFuseIgnoreEntry(content))
                    return;

                var separator = content.Length > 0 && !content.EndsWith('\n') ? Environment.NewLine : string.Empty;
                File.AppendAllText(gitIgnorePath, separator + FuseIgnoreEntry + Environment.NewLine);
                writeStep($"Added {FuseIgnoreEntry} to {gitIgnorePath}");
                return;
            }

            File.WriteAllText(gitIgnorePath, FuseIgnoreEntry + Environment.NewLine);
            writeStep($"Created {gitIgnorePath} with {FuseIgnoreEntry}");
        }
        catch (IOException ex)
        {
            writeNote($"Note: could not update .gitignore ({ex.Message}). Add {FuseIgnoreEntry} manually.");
        }
    }

    /// <summary>
    ///     Returns whether <paramref name="content" /> already contains a gitignore line that ignores <c>.fuse/</c>.
    /// </summary>
    /// <param name="content">The raw <c>.gitignore</c> file content.</param>
    /// <returns><see langword="true" /> when an equivalent ignore entry is present.</returns>
    internal static bool ContentHasFuseIgnoreEntry(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r').Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            if (MatchesFuseIgnorePattern(trimmed))
                return true;
        }

        return false;
    }

    private static bool MatchesFuseIgnorePattern(string pattern)
    {
        if (pattern.EndsWith('/'))
            pattern = pattern[..^1];

        var name = pattern.StartsWith('/') ? pattern[1..] : pattern;
        return name.Equals(".fuse", StringComparison.Ordinal);
    }
}
