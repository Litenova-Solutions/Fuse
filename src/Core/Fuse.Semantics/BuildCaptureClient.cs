using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Fuse.Indexing;

namespace Fuse.Semantics;

/// <summary>
///     The parent side of N4 tier-1 build capture: spawns the out-of-process <c>fuse-build-capture</c> worker,
///     which runs the repository build and rehydrates the semantic graph, and deserializes the graph bundle it
///     emits on stdout. The worker runs in its own process precisely so its Basic.CompilerLog Roslyn closure
///     never shares a process with this parent's MSBuildWorkspace; this client references neither, only the
///     shared <see cref="CaptureResult" /> contract.
/// </summary>
/// <remarks>
///     The worker is located by the <c>FUSE_BUILD_CAPTURE_WORKER</c> environment variable (an absolute path to
///     <c>fuse-build-capture.dll</c>) or an explicit path passed to <see cref="CaptureAsync" />; when neither is
///     set the client reports tier-1 as unavailable rather than guessing, so a deployment without the worker
///     degrades cleanly to the MSBuildWorkspace and syntax tiers.
/// </remarks>
public sealed class BuildCaptureClient
{
    private readonly string? _workerDllPath;

    /// <summary>
    ///     Initializes a new instance of the <see cref="BuildCaptureClient" /> class.
    /// </summary>
    /// <param name="workerDllPath">An explicit path to the worker dll; when null, the <c>FUSE_BUILD_CAPTURE_WORKER</c> environment variable is used.</param>
    public BuildCaptureClient(string? workerDllPath = null) =>
        _workerDllPath = workerDllPath ?? Environment.GetEnvironmentVariable("FUSE_BUILD_CAPTURE_WORKER");

    /// <summary>Whether a worker dll is configured, so tier-1 build capture can be attempted.</summary>
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_workerDllPath) && File.Exists(_workerDllPath);

    /// <summary>
    ///     Runs the worker against a build target and returns the deserialized capture result.
    /// </summary>
    /// <param name="buildTarget">The absolute path to the solution or project to build and capture.</param>
    /// <param name="timeout">The maximum time to allow the worker (build plus rehydration) to run.</param>
    /// <param name="cancellationToken">A token to cancel the capture.</param>
    /// <returns>
    ///     The capture result, or a failed result when the worker is unavailable, times out, or emits no parseable
    ///     output, so the caller falls back to a lower tier.
    /// </returns>
    public async Task<CaptureResult> CaptureAsync(string buildTarget, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            return CaptureResult.Failed("build-capture worker not configured (set FUSE_BUILD_CAPTURE_WORKER)");

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Fixed, bounded argument list (worker dll, mode, target); never a variable-length list, per the invariant.
        psi.ArgumentList.Add(_workerDllPath!);
        psi.ArgumentList.Add("--build");
        psi.ArgumentList.Add(buildTarget);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return CaptureResult.Failed($"could not start build-capture worker: {ex.Message}");
        }

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
            return CaptureResult.Failed($"build-capture worker timed out after {timeout.TotalSeconds:F0}s");
        }

        // The worker writes exactly one JSON object on stdout (the last non-empty line); parse it.
        var line = stdout.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith('{'));
        if (line is null)
            return CaptureResult.Failed("build-capture worker produced no parseable output");

        try
        {
            return JsonSerializer.Deserialize(line, BuildCaptureJsonContext.Default.CaptureResult)
                   ?? CaptureResult.Failed("build-capture worker output deserialized to null");
        }
        catch (JsonException ex)
        {
            return CaptureResult.Failed($"could not parse build-capture worker output: {ex.Message}");
        }
    }

    /// <summary>
    ///     Runs the worker to export a portable compiler log to <paramref name="complogOutPath" /> (C2): the worker
    ///     builds the target, converts the binary log to a complog (no environment block), fail-closed-scans it for
    ///     secrets, and emits the extracted graph on stdout. Returns the graph so the caller can package it in the
    ///     bundle alongside the complog. A failed result (worker unavailable, timeout, build or scan failure) means
    ///     no bundle should be written.
    /// </summary>
    /// <param name="buildTarget">The absolute path to the solution or project to build and capture.</param>
    /// <param name="complogOutPath">The absolute path the worker writes the portable compiler log to.</param>
    /// <param name="timeout">The maximum time to allow the worker to run.</param>
    /// <param name="cancellationToken">A token to cancel the capture.</param>
    /// <returns>The capture result (the extracted graph) on success, or a failed result.</returns>
    public async Task<CaptureResult> CaptureBundleAsync(
        string buildTarget, string complogOutPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            return CaptureResult.Failed("build-capture worker not configured (set FUSE_BUILD_CAPTURE_WORKER)");

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Fixed, bounded argument list (worker dll, mode, target, complog out); never a variable-length list.
        psi.ArgumentList.Add(_workerDllPath!);
        psi.ArgumentList.Add("--capture-bundle");
        psi.ArgumentList.Add(buildTarget);
        psi.ArgumentList.Add(complogOutPath);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return CaptureResult.Failed($"could not start build-capture worker: {ex.Message}");
        }

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
            return CaptureResult.Failed($"build-capture worker timed out after {timeout.TotalSeconds:F0}s");
        }

        var line = stdout.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith('{'));
        if (line is null)
            return CaptureResult.Failed("build-capture worker produced no parseable output");

        try
        {
            return JsonSerializer.Deserialize(line, BuildCaptureJsonContext.Default.CaptureResult)
                   ?? CaptureResult.Failed("build-capture worker output deserialized to null");
        }
        catch (JsonException ex)
        {
            return CaptureResult.Failed($"could not parse build-capture worker output: {ex.Message}");
        }
    }

    /// <summary>
    ///     Speculatively typechecks a proposed single-file patch via the worker (R1 <c>fuse_check</c>): the worker
    ///     builds and rehydrates the compilation, applies the patch in memory, and returns the compiler
    ///     diagnostics for the changed document.
    /// </summary>
    /// <param name="buildTarget">The absolute path to the solution or project to build and capture.</param>
    /// <param name="relativeFilePath">The repo-relative path of the file being changed.</param>
    /// <param name="newContent">The proposed full new content of that file.</param>
    /// <param name="timeout">The maximum time to allow the worker to run.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>The diagnostics, or an abstention when the worker is unavailable or cannot verify.</returns>
    public Task<CheckResult> CheckAsync(
        string buildTarget, string relativeFilePath, string newContent, TimeSpan timeout, CancellationToken cancellationToken) =>
        RunCheckAsync("--check", buildTarget, relativeFilePath, newContent, timeout, cancellationToken);

    /// <summary>
    ///     Speculatively typechecks a proposed single-file patch against a captured compiler log WITHOUT building
    ///     (C2): the worker rehydrates the compilation from the bundle's portable compiler log and applies the patch
    ///     in memory, returning the compiler diagnostics for the changed document. This is the oracle-grade check
    ///     answer on a machine that cannot restore or build the repository.
    /// </summary>
    /// <param name="complogPath">The absolute path to the bundle's portable compiler log.</param>
    /// <param name="relativeFilePath">The repo-relative path of the file being changed.</param>
    /// <param name="newContent">The proposed full new content of that file.</param>
    /// <param name="timeout">The maximum time to allow the worker to run.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>The diagnostics, or an abstention when the worker is unavailable or the file is not in the log.</returns>
    public Task<CheckResult> CheckFromComplogAsync(
        string complogPath, string relativeFilePath, string newContent, TimeSpan timeout, CancellationToken cancellationToken) =>
        RunCheckAsync("--check-complog", complogPath, relativeFilePath, newContent, timeout, cancellationToken);

    // Spawns the worker's check mode (--check builds the target; --check-complog rehydrates a captured log without
    // building) with a fixed, bounded argument list; the unbounded new content is passed via a temp file, never an
    // argument. Both modes return a CheckResult JSON object on stdout.
    private async Task<CheckResult> RunCheckAsync(
        string mode, string firstArg, string relativeFilePath, string newContent, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            return CheckResult.Abstain("build-capture worker not configured (set FUSE_BUILD_CAPTURE_WORKER)");

        var contentFile = Path.Combine(Path.GetTempPath(), $"fuse-check-content-{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(contentFile, newContent, cancellationToken);
        try
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // Fixed, bounded argument list; the (unbounded) new content is passed via the temp file, not an arg.
            psi.ArgumentList.Add(_workerDllPath!);
            psi.ArgumentList.Add(mode);
            psi.ArgumentList.Add(firstArg);
            psi.ArgumentList.Add(relativeFilePath);
            psi.ArgumentList.Add(contentFile);

            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                return CheckResult.Abstain($"could not start build-capture worker: {ex.Message}");
            }

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
                return CheckResult.Abstain($"build-capture worker timed out after {timeout.TotalSeconds:F0}s");
            }

            var outLine = stdout.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(l => l.StartsWith('{'));
            if (outLine is null)
                return CheckResult.Abstain("build-capture worker produced no parseable output");

            try
            {
                return JsonSerializer.Deserialize(outLine, BuildCaptureJsonContext.Default.CheckResult)
                       ?? CheckResult.Abstain("worker output deserialized to null");
            }
            catch (JsonException ex)
            {
                return CheckResult.Abstain($"could not parse worker output: {ex.Message}");
            }
        }
        finally
        {
            try { File.Delete(contentFile); } catch (IOException) { }
        }
    }
}
