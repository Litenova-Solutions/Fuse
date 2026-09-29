using System.Text.Json;
using System.Text.Json.Nodes;
using Fuse.Paths;

namespace Fuse.Harnesses;

/// <summary>
///     Reads and writes the files <c>fuse init</c> registers Fuse in: each harness's settings and the VS Code MCP server
///     list. A file is replaced through a temporary file beside it, so a harness never reads one half written.
/// </summary>
internal static class SettingsFile
{
    /// <summary>How many times the move onto a settings file is tried before its failure is thrown.</summary>
    internal const int MoveAttempts = 5;

    /// <summary>The wait between two attempts to move onto a settings file.</summary>
    internal static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(50);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    ///     The JSON object in the file at <paramref name="path"/>, or an empty object when the file is missing or holds no
    ///     object. Comments and trailing commas are accepted; the comments are not kept when the file is written back.
    /// </summary>
    public static JsonObject Read(string path)
    {
        if (!File.Exists(path))
            return [];
        var text = File.ReadAllText(path);
        var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return node as JsonObject ?? [];
    }

    /// <summary>The object under <paramref name="name"/> in <paramref name="parent"/>, added when it is missing or is not an object.</summary>
    public static JsonObject GetOrAddObject(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing)
            return existing;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    /// <summary>Writes <paramref name="content"/> indented to <paramref name="path"/> and returns its repository-relative path.</summary>
    public static string Write(RepoRoot root, string path, JsonObject content) =>
        WriteText(root, path, content.ToJsonString(Indented) + Environment.NewLine);

    /// <summary>
    ///     Writes <paramref name="content"/> to <paramref name="path"/> as it is, creating its directory, and returns its
    ///     repository-relative path.
    /// </summary>
    public static string WriteText(RepoRoot root, string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".fuse-tmp";
        File.WriteAllText(temp, content);
        MoveWithRetry(() => File.Move(temp, path, overwrite: true), Thread.Sleep);
        return root.PathOf(path).Relative;
    }

    /// <summary>
    ///     Runs <paramref name="move"/>, and runs it again after <see cref="MoveRetryDelay"/> while it throws
    ///     <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>, up to <see cref="MoveAttempts"/> times in
    ///     all; the last failure is thrown. On Windows a virus scanner or the search indexer can hold a file for a moment
    ///     after it was written, and replacing the file then fails with one of the two.
    /// </summary>
    /// <param name="move">Moves the temporary file onto the settings file, replacing it.</param>
    /// <param name="wait">Waits the given time; a test passes one that records it instead.</param>
    internal static void MoveWithRetry(Action move, Action<TimeSpan> wait)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                move();
                return;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < MoveAttempts)
            {
                wait(MoveRetryDelay);
            }
        }
    }
}
