namespace Fuse.Repo;

/// <summary>
///     Reads the commit HEAD points at straight from the git directory, without spawning git, so every check can
///     notice a commit or branch switch for the cost of two small file reads.
/// </summary>
internal sealed class GitHead
{
    private readonly string _gitDir;
    private readonly string _commonDir;

    public GitHead(string root)
    {
        var dotGit = Path.Combine(root, ".git");
        _gitDir = dotGit;
        if (File.Exists(dotGit))
        {
            // A worktree or submodule: ".git" is a file naming the real git directory.
            var line = File.ReadAllText(dotGit).Trim();
            if (line.StartsWith("gitdir:", StringComparison.Ordinal))
            {
                var target = line["gitdir:".Length..].Trim();
                _gitDir = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(root, target));
            }
        }

        _commonDir = _gitDir;
        var commonFile = Path.Combine(_gitDir, "commondir");
        if (File.Exists(commonFile))
        {
            var common = File.ReadAllText(commonFile).Trim();
            _commonDir = Path.GetFullPath(Path.IsPathRooted(common) ? common : Path.Combine(_gitDir, common));
        }
    }

    /// <summary>The git directory, used to ignore git's own file events.</summary>
    public string GitDirectory => _gitDir;

    /// <summary>The commit id HEAD resolves to, or null in a repository with no commits.</summary>
    public string? Read()
    {
        try
        {
            var head = File.ReadAllText(Path.Combine(_gitDir, "HEAD")).Trim();
            for (var depth = 0; depth < 5 && head.StartsWith("ref:", StringComparison.Ordinal); depth++)
            {
                var name = head["ref:".Length..].Trim();
                var resolved = ReadRef(name);
                if (resolved is null)
                    return null;
                head = resolved;
            }

            return head.Length >= 40 ? head : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private string? ReadRef(string name)
    {
        foreach (var dir in new[] { _gitDir, _commonDir })
        {
            var loose = Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(loose))
                return File.ReadAllText(loose).Trim();
        }

        var packed = Path.Combine(_commonDir, "packed-refs");
        if (!File.Exists(packed))
            return null;
        foreach (var line in File.ReadLines(packed))
        {
            if (line.Length > 41 && line[40] == ' ' && line.AsSpan(41).SequenceEqual(name))
                return line[..40];
        }

        return null;
    }
}
