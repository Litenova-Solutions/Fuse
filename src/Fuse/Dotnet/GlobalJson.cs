using System.Text.Json;

namespace Fuse.Dotnet;

/// <summary>
///     What <c>global.json</c> says about <c>dotnet test</c>. The .NET 10 SDK runs <c>dotnet test</c> on
///     Microsoft.Testing.Platform when the nearest <c>global.json</c> sets <c>test.runner</c> to it, and that mode takes
///     other arguments than VSTest's, so the client and the engine both ask.
/// </summary>
internal static class GlobalJson
{
    /// <summary>
    ///     True when the <c>global.json</c> nearest to <paramref name="directory"/>, looking up to <paramref name="root"/>,
    ///     sets <c>test.runner</c> to Microsoft.Testing.Platform. A file that is not valid JSON counts as not setting it.
    /// </summary>
    public static bool UsesTestingPlatform(string directory, string root)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var file = Path.Combine(current.FullName, "global.json");
            if (File.Exists(file))
                return RunnerIn(file) is { } runner && runner.Equals("Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase);
            if (Path.GetFullPath(current.FullName).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                break;
        }

        return false;
    }

    private static string? RunnerIn(string file)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("test", out var test) && test.ValueKind == JsonValueKind.Object
                   && test.TryGetProperty("runner", out var runner) && runner.ValueKind == JsonValueKind.String
                ? runner.GetString()
                : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
