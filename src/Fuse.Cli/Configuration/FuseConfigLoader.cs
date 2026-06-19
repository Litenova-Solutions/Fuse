using System.Text.Json;

namespace Fuse.Cli.Configuration;

/// <summary>
///     Discovers and loads <c>fuse.json</c> or <c>.fuserc</c> from a directory tree.
/// </summary>
public static class FuseConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    ///     Finds and loads the nearest config file starting from <paramref name="startDirectory" /> upward.
    /// </summary>
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
            return JsonSerializer.Deserialize<FuseConfig>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
