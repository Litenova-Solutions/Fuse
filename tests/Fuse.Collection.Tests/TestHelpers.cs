using Fuse.Collection.Models;
using Fuse.Collection.Options;

namespace Fuse.Collection.Tests;

internal static class TestHelpers
{
    public static CollectionOptions DefaultOptions(string sourceDirectory = @"C:\src") =>
        new(sourceDirectory);

    public static FileCandidate CreateCandidate(
        string relativePath,
        string content = "content",
        string? sourceDirectory = null)
    {
        sourceDirectory ??= Path.Combine(Path.GetTempPath(), "fuse-collection-tests", Guid.NewGuid().ToString("N"));
        var fullPath = Path.Combine(sourceDirectory, relativePath);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(fullPath, content);
        return new FileCandidate(fullPath, relativePath, new FileInfo(fullPath));
    }

    public static FileCandidate CreateEmptyCandidate(string relativePath, string? sourceDirectory = null)
    {
        sourceDirectory ??= Path.Combine(Path.GetTempPath(), "fuse-collection-tests", Guid.NewGuid().ToString("N"));
        var fullPath = Path.Combine(sourceDirectory, relativePath);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(fullPath, []);
        return new FileCandidate(fullPath, relativePath, new FileInfo(fullPath));
    }
}
