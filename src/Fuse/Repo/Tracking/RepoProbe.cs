using Fuse.Dotnet.Processes;
using Fuse.Paths;
namespace Fuse.Repo.Tracking;

/// <summary>Answers, without starting an engine, whether a repository has anything for Fuse to do.</summary>
internal static class RepoProbe
{
    /// <summary>
    ///     True when git knows at least one <c>.csproj</c> in the repository (tracked, or untracked and not ignored)
    ///     that discovery keeps: not matched by <c>.fuseignore</c>, not under a directory that holds a
    ///     <c>.template.config</c> folder, and not an unrestored placeholder project. Clients call this before starting
    ///     an engine, so a repository without C# projects costs one git call.
    /// </summary>
    public static async Task<bool> HasCSharpProjectsAsync(RepoRoot root, CancellationToken cancellationToken)
    {
        var ignore = FuseIgnore.Read(root);
        // The index answers for tracked projects without touching the working tree; untracked ones need a scan.
        foreach (var listing in new[] { new[] { "--cached" }, ["--others", "--exclude-standard"] })
        {
            var result = await ProcessRunner.RunAsync("git", ["-c", "core.quotepath=off", "ls-files", "-z", .. listing, "--", "*.csproj"], root.Path, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                return true; // git could not answer; let the engine report the problem.
            foreach (var relative in result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var path = root.PathOf(relative);
                if (!File.Exists(path.Absolute))
                    continue;
                if (ignore.Ignores(path))
                    continue;
                if (IsTemplateProject(root, path))
                    continue;
                if (IsTokenizedProject(path))
                    continue;
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="project"/> lies under a directory that holds a <c>.template.config</c> folder, at any level up to <paramref name="root"/>.</summary>
    internal static bool IsTemplateProject(RepoRoot root, RepoPath project)
    {
        try
        {
            var current = Path.GetDirectoryName(project.Absolute);
            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current, ".template.config")))
                    return true;
                if (root.PathOf(current) == root.PathOf(root.Path))
                    break;
                current = Path.GetDirectoryName(current);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        return false;
    }

    /// <summary>True when <paramref name="project"/> is unrestored and its file name or text holds a placeholder token.</summary>
    internal static bool IsTokenizedProject(RepoPath project)
    {
        try
        {
            var directory = Path.GetDirectoryName(project.Absolute);
            if (directory is null)
                return false;
            if (File.Exists(Path.Combine(directory, "obj", "project.assets.json")))
                return false;
            if (ContainsPlaceholder(project.FileName))
                return true;
            string text;
            try
            {
                text = File.ReadAllText(project.Absolute);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return false;
            }

            return ContainsPlaceholder(text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool ContainsPlaceholder(string value)
    {
        for (var i = 0; i + 3 < value.Length; i++)
        {
            if (value[i] == '_' && value[i + 1] == '_')
            {
                var j = i + 2;
                while (j < value.Length && IsTokenChar(value[j]))
                    j++;
                if (j > i + 2 && j + 1 < value.Length && value[j] == '_' && value[j + 1] == '_')
                    return true;
            }

            if (value[i] == '{' && value[i + 1] == '{')
            {
                var close = value.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                    return true;
            }
        }

        return false;
    }

    private static bool IsTokenChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
}
