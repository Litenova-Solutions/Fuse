using System.Collections.Immutable;
using Basic.CompilerLog.Util;
using Fuse.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

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
                // Capture the analyzers the recorded csc invocation ran (S4): the compiler log replays the full
                // invocation including /analyzer:, so the repo's configured analyzers (StyleCop, nullable, and so
                // on) are available to run against the resident compilation for CI parity, gated per call.
                var analyzers = data.GetAnalyzers(out _);
                projects.Add(new ResidentProject(projectFilePath, compilation, analyzers, data.AnalyzerOptions));
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
        var overlay = TryFork(relativeFilePath, newContent, cancellationToken);
        if (overlay is null)
            return null;

        return overlay.Value.Forked.GetSemanticModel(overlay.Value.NewTree)
            .GetDiagnostics(cancellationToken: cancellationToken)
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(ToCheckDiagnostic)
            .ToList();
    }

    /// <summary>
    ///     Speculatively typechecks a proposed single-file edit and, when requested, also runs the project's
    ///     configured analyzers against the overlay (S4, analyzer parity): the changed document's compiler
    ///     diagnostics merged with its analyzer diagnostics, so a check reports what CI's build step would enforce.
    /// </summary>
    /// <param name="relativeFilePath">The repo-relative path of the file being changed (matched by path suffix).</param>
    /// <param name="newContent">The proposed full new content of that file.</param>
    /// <param name="includeAnalyzers">
    ///     When true, the repo's configured analyzers are run with their editorconfig-mapped options and their
    ///     diagnostics for the changed document are merged in; when false this is the compiler-only overlay check.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>
    ///     The changed document's diagnostics (compiler, plus analyzers when requested), or null when the file is
    ///     not part of any held compilation.
    /// </returns>
    public async Task<IReadOnlyList<CheckDiagnostic>?> CheckOverlayAsync(
        string relativeFilePath, string newContent, bool includeAnalyzers, CancellationToken cancellationToken)
    {
        var overlay = TryFork(relativeFilePath, newContent, cancellationToken);
        if (overlay is null)
            return null;

        var (project, forked, newTree) = overlay.Value;
        var diagnostics = forked.GetSemanticModel(newTree)
            .GetDiagnostics(cancellationToken: cancellationToken)
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .ToList();

        if (includeAnalyzers && !project.Analyzers.IsDefaultOrEmpty)
        {
            var analyzerDiagnostics = await ResidentAnalyzerRunner.DiagnosticsForTreeAsync(
                forked, project.Analyzers, project.AnalyzerOptions, newTree, cancellationToken);
            diagnostics.AddRange(analyzerDiagnostics);
        }

        // Dedup by id and line so a diagnostic the compiler and an analyzer both report is not listed twice.
        return diagnostics
            .GroupBy(d => (d.Id, d.Location.GetLineSpan().StartLinePosition.Line))
            .Select(g => g.First())
            .Select(ToCheckDiagnostic)
            .ToList();
    }

    // Finds the held project whose compilation contains the file, and forks that compilation with the proposed
    // content replacing the file's syntax tree. Shared by the compiler-only and analyzer-aware overlay checks.
    private (ResidentProject Project, Compilation Forked, SyntaxTree NewTree)? TryFork(
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

            // Replacing the tree loses its editorconfig analyzer-config mapping: the compilation's
            // SyntaxTreeOptionsProvider keys severities by the original tree, so the new tree falls back to default
            // severities and an editorconfig-elevated rule stops surfacing (and a silenced one could reappear). The
            // new tree has the same path, so redirect its config queries to the original tree to preserve severities
            // for both compiler and analyzer diagnostics on the edited document (S4).
            if (project.Compilation.Options.SyntaxTreeOptionsProvider is { } inner)
            {
                forked = forked.WithOptions(
                    forked.Options.WithSyntaxTreeOptionsProvider(new ForkedTreeOptionsProvider(inner, tree, newTree)));
            }

            return (project, forked, newTree);
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

    /// <summary>
    ///     Adds a newly created file's syntax tree to the resident state, attributed to the project whose
    ///     directory is the closest ancestor of the file (the creation half of the watcher's incremental update,
    ///     S1 step 3). Never writes the tree.
    /// </summary>
    /// <param name="absoluteFilePath">The absolute path of the new file.</param>
    /// <param name="content">The file's content.</param>
    /// <param name="cancellationToken">A token to cancel the update.</param>
    /// <returns>
    ///     True when the file was attributed to a held project and added; false when no held project contains it,
    ///     or when it is already present (the caller uses <see cref="ApplyEdit" /> for an existing file).
    /// </returns>
    public bool AddDocument(string absoluteFilePath, string content, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(absoluteFilePath).Replace('\\', '/');
        var bestIndex = -1;
        var bestLength = -1;
        for (var i = 0; i < _projects.Count; i++)
        {
            var projectDir = Path.GetDirectoryName(_projects[i].ProjectFilePath);
            if (projectDir is null)
                continue;
            var normalized = Path.GetFullPath(projectDir).Replace('\\', '/').TrimEnd('/') + "/";
            if (full.StartsWith(normalized, StringComparison.OrdinalIgnoreCase) && normalized.Length > bestLength)
            {
                bestIndex = i;
                bestLength = normalized.Length;
            }
        }

        if (bestIndex < 0)
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        var compilation = _projects[bestIndex].Compilation;
        if (compilation.SyntaxTrees.Any(t => t.FilePath.Replace('\\', '/').Equals(full, StringComparison.OrdinalIgnoreCase)))
            return false; // Already present; the caller applies an edit rather than adding a duplicate.

        // Parse with the compilation's existing parse options (language version, preprocessor symbols, doc mode);
        // AddSyntaxTrees rejects a tree whose features differ from the rest of the compilation.
        var parseOptions = compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions;
        var tree = CSharpSyntaxTree.ParseText(content, parseOptions, absoluteFilePath, cancellationToken: cancellationToken);
        _projects[bestIndex] = _projects[bestIndex] with { Compilation = compilation.AddSyntaxTrees(tree) };
        return true;
    }

    /// <summary>
    ///     Removes a deleted file's syntax tree from the resident state, so later queries no longer see it (the
    ///     deletion half of the watcher's incremental update, S1 step 3). Never writes the tree.
    /// </summary>
    /// <param name="relativeFilePath">The repo-relative path of the deleted file (matched by path suffix).</param>
    /// <param name="cancellationToken">A token to cancel the update.</param>
    /// <returns>True when the file was found in a held compilation and removed; false otherwise.</returns>
    public bool RemoveDocument(string relativeFilePath, CancellationToken cancellationToken)
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

            _projects[i] = _projects[i] with { Compilation = compilation.RemoveSyntaxTrees(tree) };
            return true;
        }

        return false;
    }

    /// <summary>
    ///     The error and warning diagnostics across every held compilation as of now. This is the baseline a delta
    ///     check compares against to attribute newly introduced or resolved diagnostics to an edit (S1 step 3, the
    ///     substrate for S2's delta mode). It reports the whole resident state; a caller scoping to a file or cone
    ///     filters the result.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the computation.</param>
    /// <returns>The error and warning diagnostics, one entry per diagnostic, across all held compilations.</returns>
    public IReadOnlyList<CheckDiagnostic> GetDiagnostics(CancellationToken cancellationToken)
    {
        var results = new List<CheckDiagnostic>();
        foreach (var project in _projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var diagnostic in project.Compilation.GetDiagnostics(cancellationToken))
            {
                if (diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                    results.Add(ToCheckDiagnostic(diagnostic));
            }
        }

        return results;
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

    // Redirects analyzer-config queries for the overlay's replaced tree to the original tree, so the edited
    // document keeps the original file's editorconfig severities (both trees share the same path). Without this,
    // ReplaceSyntaxTree drops the per-tree mapping and severities fall back to defaults (S4).
    private sealed class ForkedTreeOptionsProvider(
        SyntaxTreeOptionsProvider inner, SyntaxTree originalTree, SyntaxTree replacementTree) : SyntaxTreeOptionsProvider
    {
        public override GeneratedKind IsGenerated(SyntaxTree tree, CancellationToken cancellationToken) =>
            inner.IsGenerated(Redirect(tree), cancellationToken);

        public override bool TryGetDiagnosticValue(
            SyntaxTree tree, string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity) =>
            inner.TryGetDiagnosticValue(Redirect(tree), diagnosticId, cancellationToken, out severity);

        public override bool TryGetGlobalDiagnosticValue(
            string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity) =>
            inner.TryGetGlobalDiagnosticValue(diagnosticId, cancellationToken, out severity);

        private SyntaxTree Redirect(SyntaxTree tree) =>
            ReferenceEquals(tree, replacementTree) ? originalTree : tree;
    }
}

/// <summary>
///     One rehydrated C# project held resident: its project file path and its live <see cref="Compilation" />.
/// </summary>
/// <param name="ProjectFilePath">The project file path recorded in the compiler invocation.</param>
/// <param name="Compilation">The rehydrated compilation, held live for overlay checks and incremental updates.</param>
/// <param name="Analyzers">
///     The diagnostic analyzers the recorded csc invocation ran (S4), for optional analyzer-parity checks against
///     the resident compilation; empty when the invocation configured none.
/// </param>
/// <param name="AnalyzerOptions">
///     The analyzer options rehydrated from the capture (additional files plus the editorconfig-derived analyzer
///     config), so an analyzer run maps severities exactly as the repo configures them (S4).
/// </param>
public sealed record ResidentProject(
    string ProjectFilePath,
    Compilation Compilation,
    ImmutableArray<DiagnosticAnalyzer> Analyzers,
    AnalyzerOptions AnalyzerOptions);
