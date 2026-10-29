using System.Security.Cryptography;
using System.Text;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;

namespace Fuse.Testing;

/// <summary>
///     The input key of one test class: a hash of every input a shadow run of the class reads. Two plans with the same
///     key ran the class against byte-identical inputs, so the second answers the class from the first's pass. Null when
///     any input cannot be proven unchanged: an unreadable file, or a test that reads the clock, the environment, the
///     network, or a file outside its project.
/// </summary>
internal sealed class TestClassKey
{
    /// <summary>What a test file names when the test reads the clock, the environment, randomness or the network.</summary>
    private static readonly string[] EnvironmentMarkers =
    [
        "DateTime.Now", "DateTime.UtcNow", "DateTime.Today", "DateTimeOffset.Now", "DateTimeOffset.UtcNow",
        "Stopwatch", "Environment.TickCount", "Thread.Sleep", "Task.Delay",
        "GetEnvironmentVariable", "GetFolderPath", "Environment.CurrentDirectory", "Environment.MachineName",
        "Environment.UserName", "Environment.ProcessorCount", "SpecialFolder",
        "Random.Shared", "new Random(", "RandomNumberGenerator", "Guid.NewGuid(",
        "HttpClient", "Socket", "TcpClient", "UdpClient", "WebSocket",
        "Process.Start", "GetTempPath",
    ];

    /// <summary>What a test file names when it reads or writes files.</summary>
    private static readonly string[] FileMarkers =
        ["File.", "Directory.", "FileStream", "StreamReader", "StreamWriter", "FileInfo", "DirectoryInfo"];

    /// <summary>What a file path in a test names when the file can lie outside the test project.</summary>
    private static readonly string[] OutsideMarkers =
        ["..", "GetTempPath", "GetFolderPath", "SpecialFolder", "C:\\", "C:/", "/tmp/", "/etc/", "\\\\", "~/"];

    /// <summary>The repository files whose content a shadow run of any class reads: the SDK and build defaults.</summary>
    private static readonly string[] RepoInputs = ["global.json", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"];

    // Each file's syntax facts by text version, kept across plans, so a rebuilt graph parses only the files that changed.
    private readonly Dictionary<DocumentId, (VersionStamp Version, TypeGraph.FileFacts Facts)> _facts = [];

    /// <summary>
    ///     The key for each of <paramref name="classes"/> in <paramref name="test"/> as <paramref name="snapshot"/> has
    ///     it, or null for a class that must run for real.
    /// </summary>
    /// <param name="snapshot">The plan's snapshot, which the test project's content is read from.</param>
    /// <param name="root">The repository, which key lines name files relative to.</param>
    /// <param name="graph">The project graph the snapshot was taken with.</param>
    /// <param name="test">The test project holding the classes.</param>
    /// <param name="classes">The classes, as the test names before their last dot have them.</param>
    /// <param name="head">The commit the baseline is taken from, or null in a repository without commits.</param>
    /// <param name="cancellationToken">Cancels reading file contents.</param>
    public async Task<IReadOnlyDictionary<string, string?>> KeysForAsync(
        Solution snapshot, RepoRoot root, RepoGraph graph, ProjectNode test, IReadOnlyCollection<string> classes, string? head, CancellationToken cancellationToken)
    {
        try
        {
            var involved = graph.ClosureOf(test);
            var variants = new Dictionary<ProjectId, (ProjectNode Node, Project Project)>();
            foreach (var node in involved)
            {
                foreach (var project in RepoWorkspace.ProjectsFor(snapshot, node))
                    variants.TryAdd(project.Id, (node, project));
            }

            if (!variants.Values.Any(v => v.Node.Path == test.Path))
                return classes.ToDictionary(c => c, _ => (string?)null, StringComparer.Ordinal);

            var files = await FilesAsync(variants, cancellationToken).ConfigureAwait(false);
            var projects = variants.Values.Select(v => v.Project).ToList();
            var shared = SharedLines(root, involved, projects, test, head);
            if (shared is null)
                return classes.ToDictionary(c => c, _ => (string?)null, StringComparer.Ordinal);

            var edges = EdgesOf(files);
            var visible = Visible(projects);
            var result = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var className in classes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result[className] = KeyFor(files, edges, visible, root, test, className, shared);
            }

            return result;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return classes.ToDictionary(c => c, _ => (string?)null, StringComparer.Ordinal);
        }
    }

    /// <summary>The key for one class, or null when it must run for real.</summary>
    private static string? KeyFor(
        Dictionary<string, SourceFile> files,
        Dictionary<string, List<(string File, ProjectId Project)>> edges,
        Dictionary<ProjectId, HashSet<ProjectId>> visible,
        RepoRoot root,
        ProjectNode test,
        string className,
        HashSet<string> shared)
    {
        var own = files.Where(f => f.Value.Node.Path == test.Path && Declares(f.Value.Facts, className)).Select(f => f.Key).ToList();
        if (own.Count == 0)
            return null;
        var closure = Closure(files, edges, visible, own);
        if (closure.Any(f => ReadsEnvironment(files[f].Text) || ReadsOutside(files[f].Text)))
            return null;
        var lines = new HashSet<string>(shared, StringComparer.Ordinal);
        foreach (var file in closure)
            lines.Add($"src:{root.PathOf(file).Relative}={HashOf(files[file].Text)}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines.Order(StringComparer.Ordinal)))));
    }

    /// <summary>The key lines every class of the project shares, or null when any of them cannot be proven unchanged.</summary>
    private static HashSet<string>? SharedLines(RepoRoot root, IReadOnlyList<ProjectNode> involved, IReadOnlyList<Project> variants, ProjectNode test, string? head)
    {
        var lines = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in RepoInputs)
        {
            var path = Path.Combine(root.Path, name);
            if (!File.Exists(path))
                continue;
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            lines.Add($"repo:{name}={Convert.ToHexString(SHA256.HashData(bytes))}");
        }

        foreach (var node in involved)
        {
            foreach (var input in node.EvaluationInputs.OrderBy(i => i.Relative, StringComparer.Ordinal))
            {
                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(input.Absolute);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return null;
                }

                lines.Add($"eval:{input.Relative}={Convert.ToHexString(SHA256.HashData(bytes))}");
            }

            if (FingerprintOf(root, node) is not { } fingerprint)
                return null;
            lines.UnionWith(fingerprint);
        }

        foreach (var project in variants)
        {
            foreach (var reference in project.MetadataReferences.OfType<PortableExecutableReference>())
            {
                if (reference.FilePath is not { } path || !File.Exists(path))
                    return null;
                var file = new FileInfo(path);
                string version;
                try
                {
                    version = System.Reflection.AssemblyName.GetAssemblyName(path).Version?.ToString() ?? "";
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    return null;
                }

                lines.Add($"ref:{Path.GetFileNameWithoutExtension(path)},{version},{file.Length},{file.LastWriteTimeUtc.Ticks}");
            }
        }

        if (test.RunSettings is not null)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(test.RunSettings);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            lines.Add($"runsettings={Convert.ToHexString(SHA256.HashData(bytes))}");
        }

        foreach (var name in variants.Where(v => test.Path.Matches(v.FilePath)).Select(v => v.Name).Order(StringComparer.Ordinal))
            lines.Add($"variant:{name}");
        lines.Add($"head:{head ?? ""}");
        return lines;
    }

    /// <summary>
    ///     The files of one project besides its C# sources as a fingerprint of relative path, length and write time,
    ///     with the directories, whose write time moves when the file set changes. C# sources are hashed by content
    ///     where the closure names them, so they stay out here; every other file, including Razor, is fingerprinted.
    ///     Null when any entry cannot be read.
    /// </summary>
    private static HashSet<string>? FingerprintOf(RepoRoot root, ProjectNode node)
    {
        var lines = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in EnumerateProjectEntries(node.Directory.Absolute))
        {
            string relative;
            try
            {
                relative = root.PathOf(entry).Relative;
                if (Directory.Exists(entry))
                {
                    lines.Add($"dir:{relative}={Directory.GetLastWriteTimeUtc(entry).Ticks}");
                    continue;
                }

                if (entry.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    continue;
                var file = new FileInfo(entry);
                if (!file.Exists)
                    return null;
                lines.Add($"file:{relative}={file.Length},{file.LastWriteTimeUtc.Ticks}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return lines;
    }

    /// <summary>Every directory and file under the project directory, skipping build output, hidden folders and nested projects.</summary>
    private static IEnumerable<string> EnumerateProjectEntries(string directory)
    {
        var stack = new Stack<string>([directory]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            yield return dir;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir).ToList();
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("obj", StringComparison.OrdinalIgnoreCase) || name.Equals("TestResults", StringComparison.OrdinalIgnoreCase) || name.StartsWith('.')
                        || File.Exists(Path.Combine(sub, name + ".csproj")) || Directory.EnumerateFiles(sub, "*.csproj").Any())
                        continue;
                    stack.Push(sub);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
                yield return file;
        }
    }

    /// <summary>Every compiled C# source of the variants by absolute path, with its syntax facts and its snapshot text.</summary>
    private async Task<Dictionary<string, SourceFile>> FilesAsync(Dictionary<ProjectId, (ProjectNode Node, Project Project)> variants, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, SourceFile>(StringComparer.Ordinal);
        foreach (var (node, project) in variants.Values)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath is not { } path || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || files.ContainsKey(path))
                    continue;
                var version = await document.GetTextVersionAsync(cancellationToken).ConfigureAwait(false);
                if (!_facts.TryGetValue(document.Id, out var cached) || cached.Version != version)
                {
                    var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                    cached = (version, TypeGraph.FileFacts.From(root!, document.FilePath ?? document.Name));
                    _facts[document.Id] = cached;
                }

                var text = (await document.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                files[path] = new SourceFile(project.Id, node, cached.Facts, text);
            }
        }

        return files;
    }

    /// <summary>Which files each simple type or extension-method name can refer to, per declaring file and project.</summary>
    private static Dictionary<string, List<(string File, ProjectId Project)>> EdgesOf(Dictionary<string, SourceFile> files)
    {
        var edges = new Dictionary<string, List<(string File, ProjectId Project)>>(StringComparer.Ordinal);
        foreach (var (file, source) in files)
        {
            foreach (var (name, _) in source.Facts.Declared)
                Add(edges, SimpleName(name), (file, source.Project));
            foreach (var (method, _) in source.Facts.ExtensionMethods)
                Add(edges, method, (file, source.Project));
        }

        return edges;
    }

    /// <summary>Every file the classes' files reach through the types they name, including the classes' own files.</summary>
    private static HashSet<string> Closure(
        Dictionary<string, SourceFile> files,
        Dictionary<string, List<(string File, ProjectId Project)>> edges,
        Dictionary<ProjectId, HashSet<ProjectId>> visible,
        IReadOnlyList<string> own)
    {
        var seen = new HashSet<string>(own, StringComparer.Ordinal);
        var queue = new Queue<string>(own);
        while (queue.Count > 0)
        {
            var file = queue.Dequeue();
            var sees = visible[files[file].Project];
            foreach (var identifier in files[file].Facts.Identifiers)
            {
                if (!edges.TryGetValue(identifier, out var targets))
                    continue;
                foreach (var (target, project) in targets)
                {
                    if (sees.Contains(project) && seen.Add(target))
                        queue.Enqueue(target);
                }
            }
        }

        return seen;
    }

    /// <summary>What each variant sees: itself and everything it references, directly or transitively.</summary>
    private static Dictionary<ProjectId, HashSet<ProjectId>> Visible(IReadOnlyList<Project> variants)
    {
        var visible = new Dictionary<ProjectId, HashSet<ProjectId>>();
        foreach (var project in variants)
        {
            var sees = new HashSet<ProjectId>();
            var stack = new Stack<Project>([project]);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!sees.Add(current.Id))
                    continue;
                foreach (var reference in current.ProjectReferences)
                {
                    if (current.Solution.GetProject(reference.ProjectId) is { } referenced)
                        stack.Push(referenced);
                }
            }

            visible[project.Id] = sees;
        }

        return visible;
    }

    private static void Add(Dictionary<string, List<(string File, ProjectId Project)>> edges, string name, (string File, ProjectId Project) target)
    {
        if (!edges.TryGetValue(name, out var list))
            edges[name] = list = [];
        list.Add(target);
    }

    /// <summary>True when the file's facts declare the class.</summary>
    private static bool Declares(TypeGraph.FileFacts facts, string className) =>
        facts.Declared.Any(d => d.Name == className);

    /// <summary>True when the text reads the clock, the environment, randomness or the network.</summary>
    private static bool ReadsEnvironment(string text) =>
        EnvironmentMarkers.Any(m => text.Contains(m, StringComparison.Ordinal));

    /// <summary>True when the text reads or writes a file that can lie outside the test project.</summary>
    private static bool ReadsOutside(string text) =>
        FileMarkers.Any(m => text.Contains(m, StringComparison.Ordinal))
        && OutsideMarkers.Any(m => text.Contains(m, StringComparison.Ordinal));

    private static string SimpleName(string qualified)
    {
        var last = qualified.LastIndexOfAny(['.', '+']);
        return last < 0 ? qualified : qualified[(last + 1)..];
    }

    private static string HashOf(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>One compiled source: which project compiles it, its syntax facts, and its text in the snapshot.</summary>
    private sealed record SourceFile(ProjectId Project, ProjectNode Node, TypeGraph.FileFacts Facts, string Text);
}
