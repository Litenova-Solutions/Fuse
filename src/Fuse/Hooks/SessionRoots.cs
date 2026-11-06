using System.Security.Cryptography;
using System.Text;
using Fuse.Paths;

namespace Fuse.Hooks;

/// <summary>
///     The repository roots one session's post-edit hooks touched, kept in a file per session under Fuse's local
///     state folder. A session that edits in more than one repository is checked in each of them when it stops,
///     because its stop names only the repository of its working directory. The file holds one root per line and is
///     written best effort: a root that fails to record is checked only when a later post-edit records it.
/// </summary>
internal static class SessionRoots
{
    /// <summary>The file holding the roots <paramref name="session"/> touched, under <paramref name="baseDirectory"/>.</summary>
    internal static string FileFor(string baseDirectory, string session) =>
        Path.Combine(baseDirectory, "session-roots", Hash(session) + ".txt");

    /// <summary>Records <paramref name="rootPath"/> among the roots <paramref name="session"/> touched, once however often it is recorded.</summary>
    internal static void Record(string baseDirectory, string session, string rootPath)
    {
        try
        {
            var path = FileFor(baseDirectory, session);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && File.ReadAllLines(path).Contains(rootPath, StringComparer.Ordinal))
                return;
            File.AppendAllText(path, rootPath + Environment.NewLine);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Records <paramref name="root"/> among the roots <paramref name="session"/> touched.</summary>
    internal static void Record(string session, RepoRoot root) => Record(LocalState.BaseDirectory, session, root.Path);

    /// <summary>The roots <paramref name="session"/> touched, as recorded under <paramref name="baseDirectory"/>, each once in the order first recorded.</summary>
    internal static IReadOnlyList<string> Read(string baseDirectory, string session)
    {
        try
        {
            var path = FileFor(baseDirectory, session);
            if (!File.Exists(path))
                return [];
            return [.. File.ReadAllLines(path).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    ///     The repositories a stop for <paramref name="session"/> checks: <paramref name="current"/> first, then each
    ///     recorded root that still holds a repository, each once. With no session, only <paramref name="current"/>.
    /// </summary>
    internal static IReadOnlyList<RepoRoot> RootsFor(string? session, RepoRoot current)
    {
        var roots = new List<RepoRoot> { current };
        if (session is null)
            return roots;
        foreach (var recorded in Read(LocalState.BaseDirectory, session))
        {
            if (RepoRoot.Find(recorded) is { } found && roots.All(r => r.Path != found.Path))
                roots.Add(found);
        }

        return roots;
    }

    private static string Hash(string session) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(session)))[..16];
}
