using System.Diagnostics;
using Basic.CompilerLog.Util;
using Fuse.Indexing;
using Fuse.Semantics;
using Fuse.Semantics.Analyzers;
using Microsoft.CodeAnalysis;

namespace Fuse.BuildCaptureWorker;

/// <summary>
///     Runs a repository's own build once with a binary log and rehydrates exact Roslyn compilations from the
///     recorded compiler invocations (N4 tier-1). Lives in the standalone worker executable so its
///     Basic.CompilerLog.Util Roslyn closure never shares a process with the parent's MSBuildWorkspace, which the
///     two would conflict over; this type never invokes MSBuildWorkspace.
/// </summary>
public sealed class BuildCaptureRehydrator
{
    /// <summary>
    ///     Builds the target and rehydrates its C# compilations, reporting each project's outcome.
    /// </summary>
    /// <param name="buildTarget">The absolute path to the solution or project to build.</param>
    /// <param name="buildTimeout">The maximum time to allow the build to run.</param>
    /// <param name="cancellationToken">A token to cancel the capture.</param>
    /// <returns>The capture result: the achieved outcome plus one entry per rehydrated C# compilation.</returns>
    public async Task<CaptureResult> CaptureAsync(
        string buildTarget, TimeSpan buildTimeout, CancellationToken cancellationToken)
    {
        var binlogPath = Path.Combine(Path.GetTempPath(), $"fuse-capture-{Guid.NewGuid():N}.binlog");
        try
        {
            var (exitCode, timedOut, firstError) = await RunBuildAsync(buildTarget, binlogPath, buildTimeout, cancellationToken);
            if (timedOut)
                return CaptureResult.Failed($"build timed out after {buildTimeout.TotalSeconds:F0}s");
            if (exitCode != 0 || !File.Exists(binlogPath))
                return CaptureResult.Failed(firstError is null ? $"build failed (exit {exitCode})" : $"build failed ({firstError})");

            return RehydrateFromBinlog(binlogPath, cancellationToken);
        }
        finally
        {
            TryDelete(binlogPath);
        }
    }

    /// <summary>
    ///     Builds the target and exports a portable compiler log (<c>.complog</c>) to <paramref name="complogPath" />
    ///     (C2). The complog packages the compiler inputs (source, reference closure, generated documents, and
    ///     command lines) self-contained and, unlike the binary log, WITHOUT the build's environment variables, so
    ///     it is the artifact a bundle ships. Also rehydrates and returns the extracted graph so the caller can
    ///     package the graph alongside the complog. The binary log is deleted; the complog is left at the path.
    /// </summary>
    /// <param name="buildTarget">The absolute path to the solution or project to build.</param>
    /// <param name="complogPath">The absolute path to write the portable compiler log to.</param>
    /// <param name="buildTimeout">The maximum time to allow the build to run.</param>
    /// <param name="cancellationToken">A token to cancel the capture.</param>
    /// <returns>The capture result (the extracted graph) on success, or a concrete failure; the complog is at <paramref name="complogPath" /> on success.</returns>
    public async Task<CaptureResult> ExportCompilerLogAsync(
        string buildTarget, string complogPath, TimeSpan buildTimeout, CancellationToken cancellationToken)
    {
        var binlogPath = Path.Combine(Path.GetTempPath(), $"fuse-capture-{Guid.NewGuid():N}.binlog");
        try
        {
            var (exitCode, timedOut, firstError) = await RunBuildAsync(buildTarget, binlogPath, buildTimeout, cancellationToken);
            if (timedOut)
                return CaptureResult.Failed($"build timed out after {buildTimeout.TotalSeconds:F0}s");
            if (exitCode != 0 || !File.Exists(binlogPath))
                return CaptureResult.Failed(firstError is null ? $"build failed (exit {exitCode})" : $"build failed ({firstError})");

            // Convert the binary log to the portable complog (all recorded compiler calls). The complog is a zip of
            // the compiler inputs with no environment block, which is the C2 secret posture (the binlog never ships).
            var conversion = CompilerLogUtil.ConvertBinaryLog(binlogPath, complogPath, static _ => true);
            if (!File.Exists(complogPath))
            {
                var reason = conversion.Count > 0 ? conversion[0] : "no compiler calls were recorded";
                return CaptureResult.Failed($"compiler-log export produced no file ({reason})");
            }

            // Fail-closed secret scan (C2): scan the build-injected artifacts the complog newly exposes (generated
            // documents and additional files) with the shipped redactor. Any finding, or any error that prevents a
            // complete scan, deletes the complog and fails the capture, naming the match class and artifact but
            // never the secret value. A partial capture that might ship a secret is never preferred over abstaining.
            ComplogSecretFinding? finding;
            try
            {
                finding = ComplogSecretScanner.ScanCompilerLog(complogPath, redactor: null, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TryDelete(complogPath);
                return CaptureResult.Failed($"secret scan could not complete ({ex.GetType().Name}); the compiler log was not kept (fail closed)");
            }

            if (finding is not null)
            {
                TryDelete(complogPath);
                return CaptureResult.Failed($"secret scan failed closed: a {finding.Kind} secret was detected in {finding.Label}; the compiler log was not kept");
            }

            // Rehydrate the graph from the binlog too, so the bundle carries the extracted graph next to the complog.
            return RehydrateFromBinlog(binlogPath, cancellationToken);
        }
        finally
        {
            TryDelete(binlogPath);
        }
    }

    /// <summary>
    ///     Speculatively typechecks a proposed single-file patch: builds and rehydrates the compilations, replaces
    ///     the target file's syntax tree with the proposed content in memory, and returns the compiler diagnostics
    ///     for that document (R1). No disk write of the patch, no second build.
    /// </summary>
    /// <param name="buildTarget">The absolute path to the solution or project to build and capture.</param>
    /// <param name="relativeFilePath">The repo-relative path of the file being changed.</param>
    /// <param name="newContent">The proposed full new content of that file.</param>
    /// <param name="buildTimeout">The maximum time to allow the capture build to run.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>The diagnostics for the changed document, or an abstention when capture is unavailable.</returns>
    public async Task<CheckResult> CheckAsync(
        string buildTarget, string relativeFilePath, string newContent, TimeSpan buildTimeout, CancellationToken cancellationToken)
    {
        var binlogPath = Path.Combine(Path.GetTempPath(), $"fuse-check-{Guid.NewGuid():N}.binlog");
        try
        {
            var (exitCode, timedOut, firstError) = await RunBuildAsync(buildTarget, binlogPath, buildTimeout, cancellationToken);
            if (timedOut || exitCode != 0 || !File.Exists(binlogPath))
                return CheckResult.Abstain(timedOut ? "capture build timed out" : $"capture build did not succeed ({firstError ?? $"exit {exitCode}"}); cannot verify");

            using var reader = CompilerCallReaderUtil.Create(binlogPath);
            var normalized = relativeFilePath.Replace('\\', '/');
            foreach (var data in reader.ReadAllCompilationData())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (data.CompilerCall.IsCSharp != true)
                    continue;

                var compilation = data.GetCompilationAfterGenerators(cancellationToken);
                var tree = compilation.SyntaxTrees.FirstOrDefault(t =>
                    t.FilePath.Replace('\\', '/').EndsWith(normalized, StringComparison.OrdinalIgnoreCase));
                if (tree is null)
                    continue; // The changed file is not in this project; try the next.

                var newTree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                    newContent, (Microsoft.CodeAnalysis.CSharp.CSharpParseOptions?)tree.Options, tree.FilePath, cancellationToken: cancellationToken);
                var forked = compilation.ReplaceSyntaxTree(tree, newTree);
                var diagnostics = forked.GetSemanticModel(newTree)
                    .GetDiagnostics(cancellationToken: cancellationToken)
                    .Where(d => d.Severity is Microsoft.CodeAnalysis.DiagnosticSeverity.Error or Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
                    .Select(ToCheckDiagnostic)
                    .ToList();
                return CheckResult.Ok(diagnostics);
            }

            return CheckResult.Abstain($"the changed file '{relativeFilePath}' was not found in any captured C# project");
        }
        finally
        {
            TryDelete(binlogPath);
        }
    }

    private static CheckDiagnostic ToCheckDiagnostic(Microsoft.CodeAnalysis.Diagnostic d)
    {
        var span = d.Location.IsInSource ? d.Location.GetLineSpan() : default;
        return new CheckDiagnostic(
            Id: d.Id,
            Severity: d.Severity.ToString(),
            Message: d.GetMessage(),
            FilePath: d.Location.IsInSource ? d.Location.SourceTree?.FilePath : null,
            Line: d.Location.IsInSource ? span.StartLinePosition.Line + 1 : 0);
    }

    /// <summary>
    ///     Rehydrates the C# compilations recorded in a binary log. Exposed so a test can rehydrate a binlog it
    ///     produced without re-running a build.
    /// </summary>
    /// <param name="binlogPath">The path to the binary log.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The capture result with one entry per rehydrated C# compilation.</returns>
    public CaptureResult RehydrateFromBinlog(string binlogPath, CancellationToken cancellationToken)
    {
        using var reader = CompilerCallReaderUtil.Create(binlogPath);
        var symbolExtractor = new SemanticSymbolExtractor();
        var analyzers = SemanticAnalysisRunner.CreateDefault();
        var projects = new List<CapturedProject>();
        foreach (var data in reader.ReadAllCompilationData())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = data.CompilerCall;
            if (call.IsCSharp != true)
                continue;

            var compilation = data.GetCompilationAfterGenerators(cancellationToken);
            var errorCount = compilation.GetDiagnostics(cancellationToken)
                .Count(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
            var typeCount = CountTypes(compilation.Assembly.GlobalNamespace);

            // Run Fuse's semantic extraction over the rehydrated compilation (never MSBuildWorkspace), the crux of
            // tier-1: the worker produces the same symbol and wiring-graph data the in-process semantic pass does.
            var projectDir = Path.GetDirectoryName(call.ProjectFilePath) ?? Directory.GetCurrentDirectory();
            var loaded = new LoadedProject(
                Name: Path.GetFileNameWithoutExtension(call.ProjectFilePath) ?? call.ProjectFileName ?? "project",
                FilePath: call.ProjectFilePath ?? "",
                AssemblyName: compilation.AssemblyName,
                Compilation: compilation);
            var symbols = symbolExtractor.Extract(loaded, projectDir, cancellationToken);
            var graph = analyzers.Run(new SemanticAnalysisContext(loaded, projectDir), cancellationToken);

            projects.Add(new CapturedProject(
                Name: loaded.Name,
                FilePath: loaded.FilePath,
                AssemblyName: compilation.AssemblyName,
                ErrorCount: errorCount,
                TypeCount: typeCount,
                SymbolCount: symbols.Count,
                NodeCount: graph.Nodes.Count,
                EdgeCount: graph.Edges.Count,
                Symbols: symbols,
                Nodes: graph.Nodes,
                Edges: graph.Edges,
                Routes: graph.Routes,
                DiRegistrations: graph.DiRegistrations,
                OptionsBindings: graph.OptionsBindings));
        }

        return projects.Count == 0
            ? CaptureResult.Failed("the build log recorded no C# compiler invocations")
            : CaptureResult.Ok(projects);
    }

    private static int CountTypes(INamespaceSymbol ns)
    {
        var count = ns.GetTypeMembers().Length;
        foreach (var child in ns.GetNamespaceMembers())
            count += CountTypes(child);
        return count;
    }

    // Runs `dotnet build <target> -bl:<binlog>` with a fixed, bounded argument list (never a variable-length
    // path or id list, per the change-safety invariant) and a timeout.
    private static async Task<(int ExitCode, bool TimedOut, string? FirstError)> RunBuildAsync(
        string buildTarget, string binlogPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(buildTarget) ?? Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(buildTarget);
        psi.ArgumentList.Add($"-bl:{binlogPath}");
        psi.ArgumentList.Add("-nologo");
        psi.ArgumentList.Add("-v:quiet");

        using var process = new Process { StartInfo = psi };
        var output = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (-1, true, null);
        }

        var match = System.Text.RegularExpressions.Regex.Match(output.ToString(), @"error\s+([A-Z]{2,}\d{3,})");
        return (process.ExitCode, false, match.Success ? match.Groups[1].Value : null);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
