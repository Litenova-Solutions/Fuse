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

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    ///     The JSON object in the file at <paramref name="path"/>, or an empty object when the file is missing, empty,
    ///     holds only comments, or holds a value that is not an object. Comments and trailing commas are accepted; the
    ///     comments are not kept when the file is written back.
    /// </summary>
    /// <param name="root">The repository, whose relative path of the file a failure names.</param>
    /// <param name="path">The file's absolute path.</param>
    /// <exception cref="JsonException">The file holds text that is not JSON. The message names the file and ends with the fix.</exception>
    /// <exception cref="IOException">The file cannot be read. The message names the file.</exception>
    public static JsonObject Read(RepoRoot root, string path)
    {
        if (!File.Exists(path))
            return [];
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"could not read {root.PathOf(path).Relative} ({e.Message})", e);
        }

        try
        {
            // A file with no token at all, only whitespace and comments, is an object nothing has been written to yet.
            // Read as a block that may continue, so a buffer without a token returns false rather than throwing.
            var reader = new Utf8JsonReader(bytes, isFinalBlock: false, new JsonReaderState(new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }));
            if (!reader.Read())
                return [];
            return JsonNode.Parse(bytes, documentOptions: Lenient) as JsonObject ?? [];
        }
        catch (JsonException e)
        {
            throw new JsonException($"{root.PathOf(path).Relative} is not valid JSON ({e.Message}); fix or remove it", e);
        }
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

    /// <summary>Writes <paramref name="content"/> indented to <paramref name="path"/> and returns the file and whether it changed.</summary>
    public static WrittenFile Write(RepoRoot root, string path, JsonObject content) =>
        WriteText(root, path, content.ToJsonString(Indented) + Environment.NewLine);

    /// <summary>
    ///     Writes <paramref name="content"/> to <paramref name="path"/> as it is, creating its directory, and returns the file
    ///     and whether it changed. A file that already holds exactly this content is left as it was.
    /// </summary>
    /// <exception cref="IOException">
    ///     The file could not be written or replaced. The message names the file; the file is as it was and no temporary
    ///     file is left beside it.
    /// </exception>
    public static WrittenFile WriteText(RepoRoot root, string path, string content)
    {
        var relative = root.PathOf(path).Relative;
        var temp = path + ".fuse-tmp";
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == content)
                return new WrittenFile(relative, Changed: false);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, content);
            MoveWithRetry(() => File.Move(temp, path, overwrite: true), Thread.Sleep);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The write itself failed; the message below is the one to show.
            }

            throw new IOException($"could not write {relative} ({e.Message})", e);
        }

        return new WrittenFile(relative, Changed: true);
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
