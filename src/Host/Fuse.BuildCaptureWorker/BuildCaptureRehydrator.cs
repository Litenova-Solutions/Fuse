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

/// <summary>One rehydrated C# project from a build capture.</summary>
/// <param name="Name">The project name.</param>
/// <param name="FilePath">The project file path.</param>
/// <param name="AssemblyName">The output assembly name.</param>
/// <param name="ErrorCount">The number of compile errors in the rehydrated compilation.</param>
/// <param name="TypeCount">The number of named types the compilation declares.</param>
