using System.Diagnostics;
using DotMake.CommandLine;
using Fuse.Cli.Services;
using Fuse.Indexing;
using Fuse.Semantics;

namespace Fuse.Cli.Commands;

/// <summary>
///     Produces a portable capture bundle (C2): builds the workspace once (through the out-of-process
///     build-capture worker), exports the compilation as a portable compiler log, fail-closed-scans it for
///     secrets, and packages the compiler log, the extracted graph, and a versioned manifest into a bundle
///     directory. Another machine that cannot restore or build the repository consumes the bundle with
///     <c>fuse index --from-capture</c> and gets oracle-grade answers without building.
/// </summary>
/// <remarks>
///     The bundle never embeds the build's binary log (which would carry environment variables); the portable
///     compiler log carries only the compiler inputs, and the worker fails the capture closed if a secret is
///     detected in the generated documents or additional files. The build-capture worker is located by
///     <c>FUSE_BUILD_CAPTURE_WORKER</c>; without it, capture reports that tier-1 is unavailable rather than
///     producing an empty bundle.
/// </remarks>
[CliCommand(
    Name = "capture",
    Description = "Build once and package a portable capture bundle (compiler log + graph + manifest) so another machine gets oracle-grade answers without building. Fail-closed secret scan; never ships the binary log.",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class CaptureCommand
{
    private readonly IConsoleUI _consoleUI;

    /// <summary>Initializes a new instance of the <see cref="CaptureCommand" /> class for CLI option binding only.</summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the dependency is null, so this instance must not run.</remarks>
    public CaptureCommand() : this(null!)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="CaptureCommand" /> class.</summary>
    /// <param name="consoleUI">The console UI for output.</param>
    public CaptureCommand(IConsoleUI consoleUI) => _consoleUI = consoleUI;

    /// <summary>The workspace directory to capture. Defaults to the current directory.</summary>
    [CliArgument(Description = "The workspace directory to capture. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>The output bundle directory.</summary>
    [CliOption(Name = "--out", Description = "The output bundle directory (created if absent). Holds the manifest, the portable compiler log, and the extracted graph.")]
    public string Out { get; set; } = "fuse-capture";

    /// <summary>Assemble a bundle from per-project fragment binlogs (the G4 build-target channel) instead of building.</summary>
    [CliOption(Name = "--merge", Required = false, Description = "Assemble a bundle by merging per-project fragment binary logs in this directory (the build-target channel, G4), instead of building the workspace. Produces a format-2 bundle equal in graph to a direct capture.")]
    public string? Merge { get; set; }

    /// <summary>
    ///     Runs the capture command.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the bundle has been written or an error reported.</returns>
    public async Task RunAsync(CliContext context)
    {
        var root = System.IO.Path.GetFullPath(Path);
        if (!Directory.Exists(root))
        {
            _consoleUI.WriteError($"Directory not found: {root}");
            return;
        }

        var client = new BuildCaptureClient();
        if (!client.IsAvailable)
        {
            _consoleUI.WriteError("build-capture worker not configured; set FUSE_BUILD_CAPTURE_WORKER to fuse-build-capture.dll. Nothing was written.");
            return;
        }

        if (Merge is not null)
        {
            await RunMergeAsync(client, root, context.CancellationToken);
            return;
        }

        var discovery = await new DotNetWorkspaceDiscoverer().DiscoverAsync(root, context.CancellationToken);
        var target = discovery.SolutionPath ?? discovery.ProjectPaths.FirstOrDefault();
        if (target is null)
        {
            _consoleUI.WriteError($"no solution or project found under {root} to capture.");
            return;
        }

        var outDir = System.IO.Path.GetFullPath(Out);
        var complogTemp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fuse-capture-{Guid.NewGuid():N}.complog");
        _consoleUI.WriteStep($"Capturing {target} (build + portable compiler-log export + secret scan)");

        var result = await client.CaptureBundleAsync(target, complogTemp, TimeSpan.FromMinutes(15), context.CancellationToken, root);
        if (!result.Succeeded)
        {
            TryDelete(complogTemp);
            _consoleUI.WriteError($"capture failed: {result.Reason}. No bundle was written.");
            return;
        }

        var commit = TryReadCommit(root);
        var capturedUtc = DateTime.UtcNow.ToString("O");
        var manifest = CaptureBundleIo.Write(outDir, complogTemp, result, commit, capturedUtc);

        _consoleUI.WriteResult(
            $"wrote capture bundle to {outDir}\n" +
            $"  fuse {manifest.FuseVersion}{(commit is null ? "" : $" @ {commit[..Math.Min(commit.Length, 12)]}")}, " +
            $"{manifest.Projects.Count} project(s), format v{manifest.BundleFormatVersion}\n" +
            $"  rehydrate on another machine with: fuse index --from-capture \"{outDir}\"");
    }

    // Assembles a version-2 bundle by merging per-project fragment binlogs (the G4 build-target channel): the
    // worker converts each fragment to a fail-closed-scanned compiler log, and the merged graph plus those logs
    // are packaged into the bundle. No build is run here; the fragments came from the team's own builds.
    private async Task RunMergeAsync(BuildCaptureClient client, string root, CancellationToken cancellationToken)
    {
        var fragmentsDir = System.IO.Path.GetFullPath(Merge!);
        if (!Directory.Exists(fragmentsDir))
        {
            _consoleUI.WriteError($"fragments directory not found: {fragmentsDir}. Nothing was written.");
            return;
        }

        var outDir = System.IO.Path.GetFullPath(Out);
        var complogTemp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fuse-merge-{Guid.NewGuid():N}");
        _consoleUI.WriteStep($"Merging capture fragments in {fragmentsDir} (secret scan per fragment); no build");

        var result = await client.MergeFragmentsAsync(fragmentsDir, complogTemp, TimeSpan.FromMinutes(10), cancellationToken, root);
        if (!result.Succeeded)
        {
            TryDeleteDir(complogTemp);
            _consoleUI.WriteError($"merge failed: {result.Reason}. No bundle was written.");
            return;
        }

        var complogs = Directory.Exists(complogTemp)
            ? Directory.GetFiles(complogTemp, "*.complog").OrderBy(p => p, StringComparer.Ordinal).ToList()
            : [];
        var commit = TryReadCommit(root);
        var capturedUtc = DateTime.UtcNow.ToString("O");
        var manifest = CaptureBundleIo.WriteMerged(outDir, complogs, result, commit, capturedUtc);
        TryDeleteDir(complogTemp);

        _consoleUI.WriteResult(
            $"wrote merged capture bundle to {outDir}\n" +
            $"  fuse {manifest.FuseVersion}{(commit is null ? "" : $" @ {commit[..Math.Min(commit.Length, 12)]}")}, " +
            $"{manifest.Projects.Count} project(s), format v{manifest.BundleFormatVersion} (merged from fragments)\n" +
            $"  rehydrate on another machine with: fuse index --from-capture \"{outDir}\"");
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; a leftover temp dir is harmless.
        }
    }

    // The source commit, read via `git rev-parse HEAD` in the workspace; null when git is absent or there is no HEAD.
    private static string? TryReadCommit(string root)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = root,
            };
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("HEAD");
            using var process = Process.Start(psi);
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
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
            // Best effort; a leftover temp complog is harmless.
        }
    }
}
