using System.Text.Json;
using Fuse.Graph;
namespace Fuse.Workspace.Blueprints;

/// <summary>
///     Tells a project file change the last restore does not cover apart from one it does: when the project file
///     or <c>Directory.Packages.props</c> is newer than the project's restore output and the project declares a
///     package its restore output does not list, the project must be restored before it can compile, instead of
///     loading into a broken compilation.
/// </summary>
internal static class RestoreCheck
{
    /// <summary>
    ///     The projects in <paramref name="closure"/> that declare a package their restore output does not list
    ///     while their project file or <c>Directory.Packages.props</c> is newer than that output. A missing or
    ///     unreadable restore output is not stale: the missing-restore path reports it.
    /// </summary>
    internal static List<ProjectNode> StaleProjects(IEnumerable<ProjectNode> closure)
    {
        var stale = new List<ProjectNode>();
        foreach (var node in closure)
        {
            if (node.PackageReferences.Count == 0)
                continue;
            if (IsStale(node))
                stale.Add(node);
        }

        return stale;
    }

    private static bool IsStale(ProjectNode node)
    {
        DateTime assetsWritten;
        try
        {
            var assets = new FileInfo(node.AssetsFile.Absolute);
            if (!assets.Exists)
                return false;
            assetsWritten = assets.LastWriteTimeUtc;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var newer = false;
        foreach (var input in node.EvaluationInputs)
        {
            if (!input.Equals(node.Path) && !Path.GetFileName(input.Absolute).Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase))
                continue;
            DateTime written;
            try
            {
                written = File.GetLastWriteTimeUtc(input.Absolute);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (written > assetsWritten)
            {
                newer = true;
                break;
            }
        }

        if (!newer)
            return false;
        if (LibrariesOf(node.AssetsFile.Absolute) is not { } libraries)
            return false;
        foreach (var package in node.PackageReferences)
        {
            if (!libraries.Contains(package))
                return true;
        }

        return false;
    }

    /// <summary>The package names the restore output at <paramref name="absolute"/> lists under its libraries, or null when it cannot be read.</summary>
    private static HashSet<string>? LibrariesOf(string absolute)
    {
        try
        {
            using var document = JsonDocument.Parse(BlueprintFiles.ReadText(absolute));
            if (!document.RootElement.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object)
                return null;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in libraries.EnumerateObject())
            {
                var name = library.Name;
                var slash = name.IndexOf('/');
                names.Add(slash < 0 ? name : name[..slash]);
            }

            return names;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}
