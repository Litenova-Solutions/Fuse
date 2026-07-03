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
}
