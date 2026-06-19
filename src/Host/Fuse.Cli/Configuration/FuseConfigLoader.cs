using System.Text.Json;
using Fuse.Cli.Serialization;

namespace Fuse.Cli.Configuration;

/// <summary>
///     Discovers and loads <c>fuse.json</c> or <c>.fuserc</c> from a directory tree.
/// </summary>
public static class FuseConfigLoader
{
    /// <summary>
    ///     Finds and loads the nearest <c>fuse.json</c> or <c>.fuserc</c>, searching from
    ///     <paramref name="startDirectory" /> upward to the filesystem root.
    /// </summary>
    /// <param name="startDirectory">Directory to begin the upward search from; resolved to a full path.</param>
    /// <returns>
    ///     The parsed configuration from the first file found, or <see langword="null" /> when no file exists or
    ///     the file cannot be parsed.
    /// </returns>
    /// <remarks>
    ///     In each directory <c>fuse.json</c> takes precedence over <c>.fuserc</c>. Parse and read errors are
    ///     swallowed and surfaced as a <see langword="null" /> result rather than thrown.
    /// </remarks>
    public static FuseConfig? Load(string startDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            var fuseJson = Path.Combine(directory.FullName, "fuse.json");
            if (File.Exists(fuseJson))
                return LoadFile(fuseJson);

            var fuseRc = Path.Combine(directory.FullName, ".fuserc");
            if (File.Exists(fuseRc))
                return LoadFile(fuseRc);

            directory = directory.Parent;
        }

        return null;
    }

    private static FuseConfig? LoadFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, FuseCliJsonContext.Default.FuseConfig);
        }
        catch
        {
            return null;
        }
    }
}
