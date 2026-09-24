using Fuse.Graph;
using Fuse.Repo;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;

namespace Fuse.Testing;

/// <summary>
///     Prepares a test assembly to run without MSBuild: copies the test project's last build output into a shadow
///     directory and overwrites the assemblies whose sources changed with ones emitted from the warm compilations.
/// </summary>
/// <remarks>
///     The shadow is only valid when everything outside C# sources matches the last build: no project file, import,
///     resource or content file is newer than the build output. Otherwise <see cref="TryPrepareAsync"/> returns
///     null and the caller runs <c>dotnet test</c> normally.
/// </remarks>
internal sealed class ShadowEmitter
{
    private readonly RepoRoot _root;
    private readonly RepoGraph _graph;

    public ShadowEmitter(RepoRoot root, RepoGraph graph)
    {
        _root = root;
        _graph = graph;
    }

    /// <summary>Returns the path of the runnable shadow test assembly, or null when the fast path is not safe.</summary>
    /// <param name="testProject">The Roslyn project for one target framework of the test project.</param>
    /// <param name="log">Receives the reason the fast path was refused.</param>
    /// <param name="cancellationToken">Cancels emitting.</param>
    public async Task<string?> TryPrepareAsync(Project testProject, Action<string> log, CancellationToken cancellationToken)
    {
        var solution = testProject.Solution;
        var testOutput = testProject.OutputFilePath;
        if (testOutput is null || !File.Exists(testOutput))
        {
            log($"{testProject.Name}: no build output yet");
            return null;
        }

        // Every project the test assembly loads, with the build output it would be copied from.
        var closure = Closure(testProject).ToList();
        var stale = new HashSet<ProjectId>();
        foreach (var project in closure)
        {
            if (project.OutputFilePath is null || !File.Exists(project.OutputFilePath))
            {
                log($"{project.Name}: no build output yet");
                return null;
            }

            var built = File.GetLastWriteTimeUtc(project.OutputFilePath);
            var node = project.FilePath is null ? null : _graph.Find(project.FilePath);
            if (node is null)
                return null;
            if (node.EvaluationInputs.Any(i => File.Exists(i) && File.GetLastWriteTimeUtc(i) > built))
            {
                log($"{project.Name}: project file changed since the last build");
                return null;
            }

            var (sourcesNewer, otherNewer) = Freshness(node, built);
            if (otherNewer is not null)
            {
                log($"{project.Name}: {_root.Relative(otherNewer)} changed since the last build");
                return null;
            }

            if (sourcesNewer)
                stale.Add(project.Id);
        }

        // A stale assembly's dependents inside the closure are re-emitted too, so none of them binds to a member that moved.
        var emit = new HashSet<ProjectId>(stale);
        foreach (var project in closure)
        {
            if (Closure(project).Any(p => stale.Contains(p.Id)))
                emit.Add(project.Id);
        }

        var shadow = Path.Combine(_root.StateDirectory, "shadow", $"{testProject.Name.Replace('(', '-').Replace(")", "")}");
        Mirror(Path.GetDirectoryName(testOutput)!, shadow);
        foreach (var id in emit)
        {
            var project = solution.GetProject(id)!;
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
                return null;
            var target = Path.Combine(shadow, Path.GetFileName(project.OutputFilePath!));
            var pdb = Path.ChangeExtension(target, ".pdb");
            await using var peStream = File.Create(target);
            await using var pdbStream = File.Create(pdb);
            var result = compilation.Emit(peStream, pdbStream, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: pdb), cancellationToken: cancellationToken);
            if (!result.Success)
            {
                log($"{project.Name}: does not compile; building with MSBuild to report the errors");
                return null;
            }
        }

        return Path.Combine(shadow, Path.GetFileName(testOutput));
    }

    private static IEnumerable<Project> Closure(Project project)
    {
        var seen = new HashSet<ProjectId>();
        var stack = new Stack<Project>([project]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!seen.Add(current.Id))
                continue;
            yield return current;
            foreach (var reference in current.ProjectReferences)
            {
                if (current.Solution.GetProject(reference.ProjectId) is { } referenced)
                    stack.Push(referenced);
            }
        }
    }

    /// <summary>
    ///     Whether the project's C# sources changed since its build output was written, and the first non-source file
    ///     in the project directory that did (a resource or content file the emitted assembly would not include). A
    ///     directory newer than the output means a file was added or deleted there, which also makes the sources stale.
    /// </summary>
    private static (bool SourcesNewer, string? OtherNewer) Freshness(ProjectNode node, DateTime built)
    {
        var sourcesNewer = false;
        foreach (var entry in EnumerateProjectEntries(node.Directory))
        {
            if (File.GetLastWriteTimeUtc(entry) <= built && Directory.GetLastWriteTimeUtc(entry) <= built)
                continue;
            if (Directory.Exists(entry) || ChangeTracker.IsSource(entry))
                sourcesNewer = true;
            else if (!ChangeTracker.IsProjectFile(entry))
                return (sourcesNewer, entry);
        }

        return (sourcesNewer, null);
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

    /// <summary>Makes <paramref name="target"/> a copy of <paramref name="source"/>, copying only files whose size or time differ.</summary>
    private static void Mirror(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            var from = new FileInfo(file);
            var to = new FileInfo(destination);
            if (to.Exists && to.Length == from.Length && to.LastWriteTimeUtc == from.LastWriteTimeUtc)
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            File.SetLastWriteTimeUtc(destination, from.LastWriteTimeUtc);
        }
    }
}
