using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fuse.Graph;
using Fuse.Paths;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;

namespace Fuse.Workspace;

/// <summary>
///     The summaries one repository shares: an image of a project's HEAD compilation, private members
///     included, emitted once and kept in a content-addressed cache under the shared directory, which is keyed by
///     the git common directory, so every worktree on the same commit reads the image another worktree wrote.
///     A project outside the change cone supplies its surface through the image instead of a live compilation,
///     which Roslyn maps rather than copies, so engines on one machine share those pages through the operating
///     system. Inside the cone nothing reads a summary: reach searches source, and test emit uses real compilations.
/// </summary>
/// <remarks>
///     The key covers what the image depends on, as the baseline store's key does for HEAD's errors: HEAD, the
///     Roslyn build, and per project in the compilation's transitive closure its evaluation inputs, its restore
///     output with worktree paths masked, the text of its documents, additional files and generated documents,
///     its metadata and analyzer references, its global analyzer options, its compilation and parse options, and
///     every editorconfig and globalconfig file. Anything that cannot be read, an unknown reference, or a HEAD
///     compilation with errors is a doubt, and the caller binds source instead: a summary is never taken from
///     <c>obj/ref</c>, which can be stale, and a missing or unreadable one falls back to source.
/// </remarks>
internal sealed class SummaryCache
{
    private readonly RepoRoot _root;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, MetadataReference> _references = new(StringComparer.OrdinalIgnoreCase);
    private bool _writeLogged;

    /// <param name="root">The repository, for the shared directory its summaries are kept in.</param>
    /// <param name="log">Where a write that fails is written, once.</param>
    public SummaryCache(RepoRoot root, Action<string> log)
    {
        _root = root;
        _log = log;
    }

    /// <summary>The directory the summaries of every worktree of this repository share, under the git common directory's state.</summary>
    public string Directory => Path.Combine(_root.SharedStateDirectory, "summaries");

    /// <summary>
    ///     The key the summary of <paramref name="baselineProject"/> at <paramref name="head"/> is kept under, or null
    ///     when nothing is kept for it: there is no commit, the project has no file or no node in the graph, or
    ///     anything the image depends on cannot be read or named. The caller binds source then.
    /// </summary>
    /// <param name="head">The commit the baseline is taken from.</param>
    /// <param name="baselineProject">The project at HEAD whose image the key names, in the baseline view.</param>
    /// <param name="graph">Every project in the repository, for the inputs each project was evaluated from.</param>
    /// <param name="cancellationToken">Cancels reading the project's documents.</param>
    public async Task<string?> KeyForAsync(string? head, Project baselineProject, RepoGraph graph, CancellationToken cancellationToken)
    {
        if (head is null || baselineProject.FilePath is null)
            return null;
        try
        {
            var lines = new List<string> { "1", head, typeof(Compilation).Assembly.GetName().Version?.ToString() ?? "" };
            var visited = new HashSet<ProjectId>();
            var queue = new Queue<Project>([baselineProject]);
            while (queue.Count > 0)
            {
                var project = queue.Dequeue();
                if (!visited.Add(project.Id))
                    continue;
                if (project.FilePath is null || graph.Find(_root.PathOf(project.FilePath)) is not { } node)
                    return null;
                lines.Add("project:" + node.Path.Relative + "|" + project.Name + "|" + project.AssemblyName);
                foreach (var input in node.EvaluationInputs.OrderBy(p => p.Relative, StringComparer.Ordinal))
                    lines.Add("input:" + input.Relative + "=" + HashFile(input.Absolute));
                if (HashAssets(node.AssetsFile.Absolute) is not { } assetsHash)
                    return null;
                lines.Add("assets:" + node.AssetsFile.Relative + "=" + assetsHash);
                if (!await AddProjectInputsAsync(lines, project, cancellationToken).ConfigureAwait(false))
                    return null;
                foreach (var reference in project.ProjectReferences)
                {
                    if (project.Solution.GetProject(reference.ProjectId) is not { } referenced)
                        return null;
                    queue.Enqueue(referenced);
                }
            }

            foreach (var config in ConfigFiles())
                lines.Add("config:" + config.Relative + "=" + HashFile(config.Absolute));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The lines one project of a summary key contributes: its texts, references and options. False when an input
    ///     cannot be named, so no summary is kept for the key.
    /// </summary>
    private async Task<bool> AddProjectInputsAsync(List<string> lines, Project project, CancellationToken cancellationToken)
    {
        foreach (var document in project.Documents.OrderBy(d => d.FilePath, StringComparer.Ordinal))
        {
            if (await TextLineAsync("document", document, cancellationToken).ConfigureAwait(false) is not { } line)
                return false;
            lines.Add(line);
        }

        foreach (var document in project.AdditionalDocuments.OrderBy(d => d.FilePath, StringComparer.Ordinal))
        {
            if (await TextLineAsync("additional", document, cancellationToken).ConfigureAwait(false) is not { } line)
                return false;
            lines.Add(line);
        }

        foreach (var document in (await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false)).OrderBy(d => d.FilePath, StringComparer.Ordinal))
        {
            if (await TextLineAsync("generated", document, cancellationToken).ConfigureAwait(false) is not { } line)
                return false;
            lines.Add(line);
        }

        foreach (var reference in project.MetadataReferences.OrderBy(r => r.Display, StringComparer.Ordinal))
        {
            switch (reference)
            {
                case PortableExecutableReference file when !string.IsNullOrEmpty(file.FilePath):
                    if (KeyPathFor(file.FilePath) is not { } referencePath)
                        return false;
                    var info = new FileInfo(file.FilePath);
                    if (!info.Exists)
                        return false;
                    lines.Add("reference:" + referencePath + "=" + info.Length + "@" + info.LastWriteTimeUtc.Ticks);
                    break;
                case CompilationReference compilationReference:
                    lines.Add("compilation-reference:" + compilationReference.Compilation.AssemblyName);
                    break;
                case { Display: { } display }:
                    lines.Add("reference-display:" + display);
                    break;
                default:
                    return false;
            }
        }

        foreach (var reference in project.AnalyzerReferences.OrderBy(r => r.Display, StringComparer.Ordinal))
        {
            switch (reference)
            {
                case AnalyzerFileReference file:
                    if (KeyPathFor(file.FullPath) is not { } analyzerPath)
                        return false;
                    lines.Add("analyzer:" + analyzerPath + "=" + HashFile(file.FullPath));
                    break;
                default:
                    // An analyzer loaded from memory has no file whose content can key a summary, so its project binds
                    // from source.
                    return false;
            }
        }

        var options = project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions;
        foreach (var key in options.Keys.Order(StringComparer.Ordinal))
        {
            if (options.TryGetValue(key, out var value))
                lines.Add("option:" + project.Name + "|" + key + "=" + value);
        }

        if (project.CompilationOptions is not { } compilationOptions)
            return false;
        lines.Add("compilation:" + compilationOptions.OutputKind + "|" + compilationOptions.Platform + "|" + compilationOptions.OptimizationLevel + "|" + compilationOptions.CheckOverflow + "|" + compilationOptions.Deterministic + "|" + compilationOptions.MetadataImportOptions + "|" + compilationOptions.WarningLevel);
        if (compilationOptions is CSharpCompilationOptions csharp)
            lines.Add("csharp-compilation:" + csharp.AllowUnsafe + "|" + csharp.NullableContextOptions);
        if (project.ParseOptions is not { } parseOptions)
            return false;
        lines.Add("parse:" + parseOptions.DocumentationMode + "|" + string.Join(",", parseOptions.PreprocessorSymbolNames.Order(StringComparer.Ordinal)) + "|" + string.Join(",", parseOptions.Features.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => f.Key + "=" + f.Value)));
        if (parseOptions is CSharpParseOptions csharpParse)
            lines.Add("csharp-parse:" + csharpParse.LanguageVersion);
        return true;
    }

    /// <summary>One text a summary depends on, by the content hash Roslyn computed for it. Null when its file cannot be named in the key.</summary>
    private async Task<string?> TextLineAsync(string kind, TextDocument document, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var named = document.FilePath is { } file ? KeyPathFor(file) : document.Name;
        return named is null ? null : kind + ":" + named + "=" + Convert.ToHexString(text.GetContentHash().ToArray());
    }

    /// <summary>
    ///     The reference to the summary of <paramref name="baselineProject"/> under <paramref name="key"/>, emitted
    ///     from its HEAD compilation when the cache holds none, or null when the summary cannot be used: the key
    ///     names no file, the compilation has errors, the emit fails, or the written image does not read back as
    ///     that assembly. The caller binds source then.
    /// </summary>
    /// <param name="baselineProject">The project at HEAD to emit, with its generated documents, so generated types stay as they are from source.</param>
    /// <param name="key">The key from <see cref="KeyForAsync"/>, or any name with no directory part for a caller that keys its own summaries.</param>
    /// <param name="cancellationToken">Cancels building the compilation and emitting it.</param>
    public async Task<MetadataReference?> GetOrEmitAsync(Project baselineProject, string key, CancellationToken cancellationToken)
    {
        if (baselineProject.FilePath is null || !IsKey(key))
            return null;
        if (TryGet(key, baselineProject) is { } kept)
            return kept;
        try
        {
            var compilation = await baselineProject.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
                return null;
            if (compilation.GetDiagnostics(cancellationToken).Any(d => d.Severity == DiagnosticSeverity.Error))
                return null;
            if (await DeclaresTypeInGeneratedFileAsync(baselineProject, cancellationToken).ConfigureAwait(false))
                return null;
            // A full image, not a metadata-only one: its metadata carries every member the compilation
            // declares, private members included, as a built assembly's does, so a dependent binds against
            // it with the same accessibility wording it binds against source with. Roslyn maps the file
            // as a metadata reference and never reads its bodies, so what a check holds is still the image,
            // not the project's compilation.
            using var pe = new MemoryStream();
            var result = compilation.Emit(
                pe,
                null,
                options: new EmitOptions().WithIncludePrivateMembers(true),
                cancellationToken: cancellationToken);
            if (!result.Success)
                return null;
            var path = PathFor(key, baselineProject);
            if (!Write(path, pe.ToArray()))
                return null;
            _references.TryRemove(path, out _);
            return ReferenceFor(path, compilation.AssemblyName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>
    ///     True when <paramref name="project"/> declares a type in a file a build generates (in a <c>bin</c> or
    ///     <c>obj</c> folder of the project). An error about such a type names the file that declares it, which
    ///     the metadata a summary carries cannot name, so no summary is kept for the project and a dependent
    ///     binds it from source, where the declaring file is a syntax reference.
    /// </summary>
    private static async Task<bool> DeclaresTypeInGeneratedFileAsync(Project project, CancellationToken cancellationToken)
    {
        if (Path.GetDirectoryName(project.FilePath) is not { } directory)
            return false;
        foreach (var document in project.Documents)
        {
            if (document.FilePath is not { } file || !Path.IsPathRooted(file) || !PathRules.IsBuildOutput(directory, file))
                continue;
            if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is { } root
                && (root.DescendantNodesAndSelf().OfType<BaseTypeDeclarationSyntax>().Any()
                    || root.DescendantNodesAndSelf().OfType<DelegateDeclarationSyntax>().Any()))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     The reference to the summary already kept for <paramref name="project"/> under <paramref name="key"/>, or
    ///     null when none is kept or what is kept does not read as that project's assembly. It emits nothing.
    /// </summary>
    public MetadataReference? TryGet(string key, Project project) =>
        project.FilePath is null || !IsKey(key) ? null : ReferenceFor(PathFor(key, project), project.AssemblyName);

    /// <summary>True when <paramref name="key"/> can name one directory of the cache: letters, digits, dashes and underscores only.</summary>
    internal static bool IsKey(string? key) =>
        !string.IsNullOrEmpty(key) && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The file the summary of <paramref name="project"/> is kept in under <paramref name="key"/>.</summary>
    private string PathFor(string key, Project project) =>
        Path.Combine(Directory, key, FileNameFor(project));

    private static string FileNameFor(Project project) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(project.FilePath + "\n" + project.Name))) + ".dll";

    /// <summary>
    ///     The reference to the image at <paramref name="path"/>, read as metadata first so an unreadable or wrong
    ///     image is no reference at all, or null then. A reference is kept per path, so a view that is derived
    ///     again reads the image Roslyn has mapped already.
    /// </summary>
    private MetadataReference? ReferenceFor(string path, string? assemblyName)
    {
        if (_references.TryGetValue(path, out var kept))
            return kept;
        try
        {
            if (!File.Exists(path) || AssemblyNameOf(path) is not { } name)
                return null;
            if (!string.IsNullOrEmpty(assemblyName) && !string.Equals(name, assemblyName, StringComparison.Ordinal))
                return null;
            var reference = MetadataReference.CreateFromFile(path);
            _references[path] = reference;
            return reference;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or BadImageFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The simple name of the assembly in the image at <paramref name="path"/>, or null when it holds no assembly.</summary>
    private static string? AssemblyNameOf(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
            return null;
        var reader = pe.GetMetadataReader();
        return reader.IsAssembly ? reader.GetString(reader.GetAssemblyDefinition().Name) : null;
    }

    /// <summary>
    ///     Writes <paramref name="bytes"/> to <paramref name="file"/> through a staging file and the key directory's
    ///     write lock, as the baseline store writes, so engines of every worktree share the cache without tearing
    ///     an image another engine is mapping. False when the write is dropped or fails; the summary is emitted
    ///     again by a later caller, and the failure is logged once.
    /// </summary>
    private bool Write(string file, byte[] bytes)
    {
        var staging = file + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        try
        {
            var directory = Path.GetDirectoryName(file)!;
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllBytes(staging, bytes);
            using var locked = AcquireWriteLock(directory);
            if (locked is null)
            {
                TryDelete(staging);
                return false;
            }

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(staging, file, overwrite: true);
                    return true;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(staging);
            if (!_writeLogged)
            {
                _writeLogged = true;
                _log($"summary write failed: {e.Message}");
            }

            return false;
        }
    }

    /// <summary>How many times a write waits for another engine's write lock before it drops the write.</summary>
    private const int WriteLockAttempts = 20;

    /// <summary>How long a write waits between write lock attempts.</summary>
    private const int WriteLockRetryMs = 50;

    /// <summary>
    ///     Takes the write lock of <paramref name="directory"/>, waiting while another engine holds it, or null when
    ///     it stays held past the wait. The operating system owns the lock, so an engine that is killed holding it
    ///     releases it at once.
    /// </summary>
    private static FileStream? AcquireWriteLock(string directory)
    {
        var path = Path.Combine(directory, "write.lock");
        for (var attempt = 0; attempt < WriteLockAttempts; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (File.Exists(path))
            {
                Thread.Sleep(WriteLockRetryMs);
            }
        }

        return null;
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Deletes every kept key but the two most recently written, so the cache stays bounded with no setting.</summary>
    /// <param name="sharedStateDirectory">The shared directory the summaries are kept under, as <see cref="Directory"/> names it for one repository.</param>
    public static void Prune(string sharedStateDirectory)
    {
        try
        {
            var directory = Path.Combine(sharedStateDirectory, "summaries");
            if (!System.IO.Directory.Exists(directory))
                return;
            foreach (var key in System.IO.Directory.EnumerateDirectories(directory).Select(d => (Path: d, Written: LatestWrite(d))).OrderByDescending(k => k.Written).Skip(2).ToList())
            {
                try
                {
                    System.IO.Directory.Delete(key.Path, recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>When the key folder was last written to, by its files or itself; a folder that cannot be read counts as the oldest.</summary>
    private static DateTime LatestWrite(string directory)
    {
        try
        {
            var latest = System.IO.Directory.GetLastWriteTimeUtc(directory);
            foreach (var file in System.IO.Directory.EnumerateFiles(directory))
            {
                var written = File.GetLastWriteTimeUtc(file);
                if (written > latest)
                    latest = written;
            }

            return latest;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>Every editorconfig and globalconfig file under the root, which set analyzer severities no project file names.</summary>
    private List<RepoPath> ConfigFiles()
    {
        var result = new List<RepoPath>();
        foreach (var name in new[] { ".editorconfig", ".globalconfig" })
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(_root.Path, name, SearchOption.AllDirectories))
                result.Add(_root.PathOf(file));
        }

        result.Sort((a, b) => string.Compare(a.Relative, b.Relative, StringComparison.Ordinal));
        return result;
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(SharedRead.AllBytes(path)));

    /// <summary>
    ///     How <paramref name="absolute"/> is named in a key: its repository-relative form when it lies inside the
    ///     repository, so worktrees name it alike, and itself otherwise. Null when it cannot be named.
    /// </summary>
    private string? KeyPathFor(string absolute)
    {
        try
        {
            var relative = _root.PathOf(absolute).Relative;
            return relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? absolute : relative;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The hash of the restore output at <paramref name="path"/>, with every path under the repository replaced
    ///     by its repository-relative form, so worktrees hash it alike. Null when it cannot be hashed.
    /// </summary>
    private string? HashAssets(string path)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not { } assets)
                return HashFile(path);
            MaskWorktreePaths(assets, _root);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(assets.ToJsonString())));
        }
        catch (Exception e) when (e is ArgumentException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Replaces every string under <paramref name="node"/> that names a path under <paramref name="root"/> with its repository-relative form.</summary>
    private static void MaskWorktreePaths(JsonNode node, RepoRoot root)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(pair => pair.Key).ToList())
            {
                if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && MaskedPath(text, root) is { } masked)
                    obj[key] = JsonValue.Create(masked);
                else if (obj[key] is { } child)
                    MaskWorktreePaths(child, root);
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue value && value.TryGetValue<string>(out var text) && MaskedPath(text, root) is { } masked)
                    array[i] = JsonValue.Create(masked);
                else if (array[i] is { } child)
                    MaskWorktreePaths(child, root);
            }
        }
    }

    /// <summary>The repository-relative form of <paramref name="value"/> when it names a path under <paramref name="root"/>, or null otherwise.</summary>
    private static string? MaskedPath(string value, RepoRoot root) =>
        value.Length > root.Path.Length && root.Contains(value) ? root.PathOf(value).Relative : null;
}
