namespace Fuse.Workspace.Blueprints;

/// <summary>
///     What a loaded project's restore output looked like when the project loaded: its length and last write time.
///     The loader notes it for every project it opens, and every sync stats the loaded projects' restore outputs
///     again; a stamp that differs means a restore ran after the load, so the workspace evaluates the projects
///     again the way a project file change does. A missing or unreadable file reads as the earliest stamp, so a
///     restore that creates it changes the stamp.
/// </summary>
internal readonly record struct AssetsStamp(long Length, DateTime WrittenUtc) : IComparable<AssetsStamp>
{
    /// <summary>Reads the stamp of the file at <paramref name="absolute"/>.</summary>
    internal static AssetsStamp Read(string absolute)
    {
        try
        {
            var info = new FileInfo(absolute);
            if (!info.Exists)
                return new AssetsStamp(-1, DateTime.MinValue);
            return new AssetsStamp(info.Length, info.LastWriteTimeUtc);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new AssetsStamp(-1, DateTime.MinValue);
        }
    }

    public int CompareTo(AssetsStamp other)
    {
        var byTime = WrittenUtc.CompareTo(other.WrittenUtc);
        return byTime != 0 ? byTime : Length.CompareTo(other.Length);
    }
}
