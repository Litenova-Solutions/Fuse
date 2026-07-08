using Basic.CompilerLog.Util;
using Fuse.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Fuse.Workspace;

/// <summary>
///     A long-lived in-memory model of a solution's compilations, rehydrated once from a tier-1 build capture
///     (a binary log) and held between calls so answers reflect the tree without re-running a build (S1, the
///     resident workspace). This is the first sub-step of S1: it holds the live <see cref="Compilation" />s that
///     the incremental watcher, delta check, and session overlays are built on, replacing the
///     rehydrate-then-discard pattern of the one-shot build-capture worker.
/// </summary>
/// <remarks>
///     <para>
///         This type references the <c>Basic.CompilerLog.Util</c> rehydration closure and never
///         <c>MSBuildWorkspace</c>, so a process hosting a resident workspace loads only the rehydration closure
///         (Decisions D7 and D8). The compiler-log reader is kept alive for the lifetime of the workspace because
///         a rehydrated compilation resolves some of its inputs lazily through the reader; <see cref="Dispose" />
///         releases both the held compilations and the reader together.
///     </para>
///     <para>
///         Overlay checks (<see cref="CheckOverlay" />) apply a proposed file's content to the held compilation in
///         memory via <see cref="Compilation.ReplaceSyntaxTree" /> and never write the tree, so a speculative edit
///         is verified against the resident compilation without a build and without touching disk.
///     </para>
/// </remarks>
public sealed class ResidentWorkspace : IDisposable
{
    private readonly ICompilerCallReader _reader;
    private readonly List<ResidentProject> _projects;

    private ResidentWorkspace(ICompilerCallReader reader, List<ResidentProject> projects)
    {
        _reader = reader;
        _projects = projects;
    }

    /// <summary>The rehydrated C# projects held resident, one per recorded C# compiler invocation.</summary>
    public IReadOnlyList<ResidentProject> Projects => _projects;

    /// <summary>
    ///     Rehydrates the C# compilations recorded in a binary log and holds them resident.
    /// </summary>
    /// <param name="binlogPath">The path to the binary log produced by a tier-1 capture build.</param>
    /// <param name="cancellationToken">A token to cancel the load.</param>
    /// <returns>A resident workspace holding one entry per rehydrated C# compilation.</returns>
    /// <exception cref="ArgumentException">The binlog path is null or empty.</exception>
    public static ResidentWorkspace LoadFromBinlog(string binlogPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(binlogPath))
            throw new ArgumentException("A binlog path is required.", nameof(binlogPath));

        // The reader is retained (not disposed here): a rehydrated compilation reads inputs lazily through it, so
        // it must outlive the load and is released only in Dispose.
        var reader = CompilerCallReaderUtil.Create(binlogPath);
        var projects = new List<ResidentProject>();
        try
        {
            foreach (var data in reader.ReadAllCompilationData())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (data.CompilerCall.IsCSharp != true)
                    continue;

                var compilation = data.GetCompilationAfterGenerators(cancellationToken);
                var projectFilePath = data.CompilerCall.ProjectFilePath ?? data.CompilerCall.ProjectFileName ?? "project";
                projects.Add(new ResidentProject(projectFilePath, compilation));
            }
        }
        catch
        {
            reader.Dispose();
            throw;
        }

        return new ResidentWorkspace(reader, projects);
    }

    /// <summary>
    ///     Speculatively typechecks a proposed single-file edit against the held compilations: applies the new
    ///     content to the file's syntax tree in memory and returns the error and warning diagnostics for that
    ///     document, without a build and without writing the tree.
    /// </summary>
    /// <param name="relativeFilePath">The repo-relative path of the file being changed (matched by path suffix).</param>
    /// <param name="newContent">The proposed full new content of that file.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>
    ///     The changed document's diagnostics, or null when the file is not part of any held compilation (the
    ///     caller decides whether that is an abstention or a fall-through to another grade).
    /// </returns>
    public IReadOnlyList<CheckDiagnostic>? CheckOverlay(
        string relativeFilePath, string newContent, CancellationToken cancellationToken)
    {
        var normalized = relativeFilePath.Replace('\\', '/');
        foreach (var project in _projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tree = project.Compilation.SyntaxTrees.FirstOrDefault(t =>
                t.FilePath.Replace('\\', '/').EndsWith(normalized, StringComparison.OrdinalIgnoreCase));
            if (tree is null)
                continue;

            var newTree = CSharpSyntaxTree.ParseText(
                newContent, (CSharpParseOptions?)tree.Options, tree.FilePath, cancellationToken: cancellationToken);
            var forked = project.Compilation.ReplaceSyntaxTree(tree, newTree);
            return forked.GetSemanticModel(newTree)
                .GetDiagnostics(cancellationToken: cancellationToken)
                .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                .Select(ToCheckDiagnostic)
                .ToList();
        }

        return null;
    }

    /// <summary>
    ///     Applies an edit to the resident state: replaces the file's syntax tree in the held compilation so every
    ///     later query and overlay check reflects the new content. This is the in-memory core of the incremental
    ///     update the file watcher drives (S1 step 3); unlike <see cref="CheckOverlay" />, which forks a throwaway
    ///     compilation for a speculative edit, this mutates the retained compilation. It never writes the tree.
    /// </summary>
    /// <param name="relativeFilePath">The repo-relative path of the edited file (matched by path suffix).</param>
    /// <param name="newContent">The new content of that file.</param>
    /// <param name="cancellationToken">A token to cancel the update.</param>
    /// <returns>True when the file was found in a held compilation and the edit was applied; false otherwise.</returns>
    public bool ApplyEdit(string relativeFilePath, string newContent, CancellationToken cancellationToken)
    {
        var normalized = relativeFilePath.Replace('\\', '/');
        for (var i = 0; i < _projects.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = _projects[i].Compilation;
            var tree = compilation.SyntaxTrees.FirstOrDefault(t =>
                t.FilePath.Replace('\\', '/').EndsWith(normalized, StringComparison.OrdinalIgnoreCase));
            if (tree is null)
                continue;

            var newTree = CSharpSyntaxTree.ParseText(
                newContent, (CSharpParseOptions?)tree.Options, tree.FilePath, cancellationToken: cancellationToken);
            _projects[i] = _projects[i] with { Compilation = compilation.ReplaceSyntaxTree(tree, newTree) };
            return true;
        }

        return false;
    }

    private static CheckDiagnostic ToCheckDiagnostic(Diagnostic diagnostic)
    {
        var inSource = diagnostic.Location.IsInSource;
        var line = inSource ? diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1 : 0;
        return new CheckDiagnostic(
            diagnostic.Id,
            diagnostic.Severity.ToString(),
            diagnostic.GetMessage(),
            inSource ? diagnostic.Location.SourceTree?.FilePath : null,
            line);
    }

    /// <summary>Releases the held compilations and the underlying compiler-log reader.</summary>
    public void Dispose() => _reader.Dispose();
}

/// <summary>
///     One rehydrated C# project held resident: its project file path and its live <see cref="Compilation" />.
/// </summary>
/// <param name="ProjectFilePath">The project file path recorded in the compiler invocation.</param>
/// <param name="Compilation">The rehydrated compilation, held live for overlay checks and incremental updates.</param>
public sealed record ResidentProject(string ProjectFilePath, Compilation Compilation);
