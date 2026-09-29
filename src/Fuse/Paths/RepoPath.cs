namespace Fuse.Paths;

/// <summary>
///     A file or directory of the repository, by its absolute path. Two values are equal when they name the same entry
///     the way the file system compares paths: ignoring case on Windows, ordinally elsewhere. So a
///     <see cref="HashSet{T}"/> or <see cref="Dictionary{TKey, TValue}"/> of paths needs no comparer.
/// </summary>
/// <remarks>
///     <para>
///         Every value comes from <see cref="RepoRoot.PathOf"/>, which canonicalizes a path given through another
///         spelling of the root (a junction, a symlink, a <c>subst</c> drive), so one file has one value. A path outside
///         the root is a value too, such as a project a <c>ProjectReference</c> names in a sibling folder; its
///         <see cref="Relative"/> form starts with <c>../</c>.
///     </para>
///     <para>
///         The hash code is computed once, when the value is created, because paths are looked up far more often than
///         they are created. The default value holds no path; no root returns it.
///     </para>
/// </remarks>
internal readonly struct RepoPath : IEquatable<RepoPath>
{
    private readonly RepoRoot _root;
    private readonly int _hash;

    /// <summary>Only <see cref="RepoRoot.PathOf"/> calls this, with the absolute path it has already canonicalized.</summary>
    internal RepoPath(RepoRoot root, string absolute)
    {
        _root = root;
        Absolute = absolute;
        _hash = absolute.GetHashCode(Comparison);
    }

    /// <summary>
    ///     How the file system compares two spellings of a path: ignoring case on Windows, ordinally elsewhere. Code outside
    ///     <c>Fuse.Paths</c> compares <see cref="RepoPath"/> values instead of strings.
    /// </summary>
    internal static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The absolute path, in the spelling the root gave it. File system, Roslyn and MSBuild calls take this.</summary>
    public string Absolute { get; }

    /// <summary>The path relative to the repository root with forward slashes, as git and the wire write it.</summary>
    public string Relative => Path.GetRelativePath(_root.Path, Absolute).Replace('\\', '/');

    /// <summary>The last segment of the path: the file name with its extension, or the directory name.</summary>
    public string FileName => Path.GetFileName(Absolute);

    public static bool operator ==(RepoPath left, RepoPath right) => left.Equals(right);

    public static bool operator !=(RepoPath left, RepoPath right) => !left.Equals(right);

    /// <summary>True when this path lies inside <paramref name="directory"/>, at any depth. A directory is not inside itself.</summary>
    public bool IsUnder(RepoPath directory)
    {
        var parent = directory.Absolute;
        var length = parent.Length;
        while (length > 0 && IsSeparator(parent[length - 1]))
            length--;
        return Absolute.Length > length
               && IsSeparator(Absolute[length])
               && Absolute.AsSpan(0, length).Equals(parent.AsSpan(0, length), Comparison);
    }

    /// <summary>
    ///     True when <paramref name="absolute"/> is this path in the same spelling, compared the way the file system does.
    ///     It canonicalizes nothing, so it is for a path Roslyn or MSBuild reports, which keeps the spelling the evaluation
    ///     gave the project; <see cref="RepoRoot.PathOf"/> turns any other spelling into a value to compare.
    /// </summary>
    public bool Matches(string? absolute) => string.Equals(Absolute, absolute, Comparison);

    public bool Equals(RepoPath other) => _hash == other._hash && string.Equals(Absolute, other.Absolute, Comparison);

    public override bool Equals(object? obj) => obj is RepoPath other && Equals(other);

    public override int GetHashCode() => _hash;

    /// <summary>The absolute path, so a log line or a failed assertion names the file.</summary>
    public override string ToString() => Absolute;

    private static bool IsSeparator(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
}
