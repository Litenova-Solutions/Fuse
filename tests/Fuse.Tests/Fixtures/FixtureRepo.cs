using System.Diagnostics;
using Fuse.Paths;

namespace Fuse.Tests.Fixtures;

/// <summary>
///     A real git repository with restored and built .NET projects in a temp directory, copied from a template
///     that is created once per test run. Disposing deletes the directory.
/// </summary>
internal sealed class FixtureRepo : IDisposable
{
    private static readonly Lazy<RepoRoot> Checkout = new(() =>
        RepoRoot.Find(AppContext.BaseDirectory) ?? throw new InvalidOperationException($"no git repository above {AppContext.BaseDirectory}"));

    private FixtureRepo(string path)
    {
        Path = path;
        Root = RepoRoot.Find(path) ?? throw new InvalidOperationException($"no repository at {path}");
    }

    /// <summary>The directory as created (possibly an 8.3 or non-canonical spelling).</summary>
    public string Path { get; }

    public RepoRoot Root { get; }

    /// <summary>
    ///     The root of the Fuse checkout the tests run from, for a test that builds repository paths but reads no file
    ///     through them, so it needs no repository of its own.
    /// </summary>
    public static RepoRoot CheckoutRoot => Checkout.Value;

    /// <summary>Copies the standard template (see <see cref="FixtureTemplate"/>) and restores it in its new location.</summary>
    public static FixtureRepo CreateStandard() => CreateStandard(NewDirectory());

    /// <summary>The standard template restored under <paramref name="target"/>, for a path whose spelling matters.</summary>
    public static FixtureRepo CreateStandard(string target)
    {
        var template = FixtureTemplate.Standard.Value;
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

    /// <summary>The repository path of a repository-relative file, as the engine names it.</summary>
    public RepoPath PathOf(string relative) => Root.PathOf(relative);

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

    /// <summary>
    ///     A directory whose own name is not ASCII, under <c>fuse-tests</c>. Everything else about the repository is the
    ///     standard template, so a difference in behaviour can only come from the path.
    /// </summary>
    internal static string NewNonAsciiDirectory()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fuse-tests", "blåbærgrød-" + Guid.NewGuid().ToString("N")[..8]);
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

    /// <summary>
    ///     Deletes the repository and its state directory. An in-process engine records no root, so without this every
    ///     test run would leave state directories in the user's local application data that no cleanup can remove.
    /// </summary>
    public void Dispose()
    {
        DeleteDirectory(Path);
        DeleteDirectory(Root.StateDirectory);
    }

    private static void DeleteDirectory(string directory)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory))
                    return;
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A BuildHost, test host or engine may still be releasing files.
                Thread.Sleep(300);
            }
        }
    }
}
