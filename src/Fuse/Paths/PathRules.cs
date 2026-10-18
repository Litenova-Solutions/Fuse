namespace Fuse.Paths;

/// <summary>
///     Which files are sources, project inputs or build output. The client applies these rules to the files a hook
///     reports as edited before it asks the engine anything, and the engine applies the same rules to what its file
///     watcher sees, so both sides agree on which files a check covers. How two paths compare is
///     <see cref="RepoPath"/>'s.
/// </summary>
internal static class PathRules
{
    private static readonly string[] SourceExtensions = [".cs", ".razor", ".cshtml"];
    private static readonly string[] ProjectExtensions = [".csproj", ".props", ".targets", ".editorconfig", ".globalconfig"];

    /// <summary>True for files the compiler reads as sources: C#, Razor components and Razor views.</summary>
    public static bool IsSource(string path) =>
        SourceExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>True for files that change how projects evaluate.</summary>
    public static bool IsProjectFile(string path)
    {
        var name = Path.GetFileName(path);
        return ProjectExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase))
               || name.Equals("global.json", StringComparison.OrdinalIgnoreCase)
               || name.Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="path"/> lies in a <c>bin</c> or <c>obj</c> folder under <paramref name="directory"/>.</summary>
    public static bool IsBuildOutput(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     The part of <paramref name="location"/> under <paramref name="root"/> with forward slashes, or null when it
    ///     is not under the root. Either separator spells the paths, because output Fuse reads, such as a stack frame,
    ///     names them in the spelling of the operating system that produced it, with text such as <c>:line 8</c> after
    ///     the path, which stays in the result.
    /// </summary>
    public static string? UnderRoot(string root, string location)
    {
        var prefix = root.Replace('\\', '/').TrimEnd('/') + "/";
        var normalized = location.Replace('\\', '/');
        return normalized.StartsWith(prefix, RepoPath.Comparison) ? normalized[prefix.Length..] : null;
    }
}
