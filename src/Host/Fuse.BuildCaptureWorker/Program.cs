using System.Text.Json;
using Fuse.BuildCaptureWorker;

// The build-capture worker: a standalone process the main Fuse process spawns for N4 tier-1, so the
// Basic.CompilerLog Roslyn closure never shares a process with the parent's MSBuildWorkspace.
//
//   fuse-build-capture --build <solutionOrProject>   run the build, rehydrate, emit JSON
//   fuse-build-capture --binlog <path>               rehydrate an existing binary log, emit JSON
//
// Output is a single JSON object on stdout: { "succeeded": bool, "reason": string?, "projects": [...] }.
// Exit code 0 on a successful capture, 1 otherwise; diagnostics go to stderr so stdout stays pure JSON.

if (args.Length < 2 || args[0] is not ("--build" or "--binlog"))
{
    await Console.Error.WriteLineAsync("usage: fuse-build-capture (--build <target> | --binlog <path>)");
    return 2;
}

var rehydrator = new BuildCaptureRehydrator();
CaptureResult result;
try
{
    result = args[0] == "--build"
        ? await rehydrator.CaptureAsync(args[1], TimeSpan.FromMinutes(10), CancellationToken.None)
        : rehydrator.RehydrateFromBinlog(args[1], CancellationToken.None);
}
catch (Exception ex)
{
    result = CaptureResult.Failed($"capture error: {ex.Message}");
}

var json = JsonSerializer.Serialize(result, BuildCaptureJsonContext.Default.CaptureResult);
Console.Out.WriteLine(json);
return result.Succeeded ? 0 : 1;
