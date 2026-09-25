using System.Diagnostics;
using Fuse.Repo;

namespace Fuse.Tests.Fixtures;

/// <summary>
///     A real git repository with restored and built .NET projects in a temp directory, copied from a template
///     that is created once per test run. Disposing deletes the directory.
/// </summary>
internal sealed class FixtureRepo : IDisposable
{
    private FixtureRepo(string path)
    {
        Path = path;
        Root = RepoRoot.Find(path) ?? throw new InvalidOperationException($"no repository at {path}");
    }

    /// <summary>The directory as created (possibly an 8.3 or non-canonical spelling).</summary>
    public string Path { get; }

    public RepoRoot Root { get; }

    /// <summary>Copies the standard template (see <see cref="FixtureTemplate"/>) and restores it in its new location.</summary>
    public static FixtureRepo CreateStandard()
    {
        var template = FixtureTemplate.Standard.Value;
        var target = NewDirectory();
        CopyDirectory(template, target);
        // project.assets.json and the generated nuget props hold absolute paths, so the copy is restored in place.
        // Every package is already in the local cache, so this is an offline no-op restore of a few seconds.
        Run(target, "dotnet", "restore", FixtureTemplate.SolutionFile, "-nologo", "-v:q");
        return new FixtureRepo(target);
    }

    /// <summary>Creates an empty repository with one commit of <paramref name="files"/>, without restoring.</summary>
    public static FixtureRepo CreateEmpty(IReadOnlyDictionary<string, string> files)
    {
        var target = NewDirectory();
        foreach (var (relative, content) in files)
        {
            var path = System.IO.Path.Combine(target, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Lf(content));
        }

        GitInit(target);
        return new FixtureRepo(target);
    }

    public string Full(string relative) => System.IO.Path.GetFullPath(System.IO.Path.Combine(Root.Path, relative));

    public string Read(string relative) => Lf(File.ReadAllText(Full(relative)));

    /// <summary>Writes a file with LF line endings, so edits written as C# literals match regardless of checkout settings.</summary>
    public void Write(string relative, string content)
    {
        var path = Full(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Lf(content));
    }

    internal static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    public void Replace(string relative, string oldText, string newText)
    {
        oldText = Lf(oldText);
        newText = Lf(newText);
        var content = Read(relative);
        if (!content.Contains(oldText, StringComparison.Ordinal))
            throw new InvalidOperationException($"'{oldText}' not found in {relative}");
        Write(relative, content.Replace(oldText, newText, StringComparison.Ordinal));
    }

    public void Delete(string relative) => File.Delete(Full(relative));

    public void Commit(string message = "change")
    {
        Run(Root.Path, "git", "add", "-A");
        Run(Root.Path, "git", "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", message);
    }


    internal static string NewDirectory()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fuse-tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(path);
        return path;
    }

    internal static void GitInit(string directory)
    {
        Run(directory, "git", "init", "-q");
        Run(directory, "git", "config", "core.autocrlf", "false");
        Run(directory, "git", "add", "-A");
        Run(directory, "git", "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "init");
    }

    internal static string Run(string directory, string file, params string[] arguments)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{file} {string.Join(' ', arguments)} failed in {directory}:\n{stdout.Result}\n{stderr.Result}");
        return stdout.Result;
    }

    internal static void CopyDirectory(string source, string target)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(System.IO.Path.Combine(target, System.IO.Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = System.IO.Path.Combine(target, System.IO.Path.GetRelativePath(source, file));
            File.Copy(file, destination, overwrite: true);
            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(file));
        }
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!Directory.Exists(Path))
                    return;
                foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A BuildHost or test host may still be releasing files.
                Thread.Sleep(300);
            }
        }
    }
}
