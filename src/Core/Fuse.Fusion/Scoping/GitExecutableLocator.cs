namespace Fuse.Fusion.Scoping;

/// <summary>
///     Locates the git executable on the process <c>PATH</c>.
/// </summary>
public static class GitExecutableLocator
{
    /// <summary>
    ///     Returns the absolute path to a <c>git</c> executable, or <see langword="null" /> when git is not on
    ///     <c>PATH</c>.
    /// </summary>
    /// <returns>The git executable path, or <see langword="null" /> when unavailable.</returns>
    public static string? Find()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", "" }
            : new[] { "" };

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, "git" + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}
