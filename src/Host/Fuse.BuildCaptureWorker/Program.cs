using System.Text.Json;
using Fuse.BuildCaptureWorker;
using Fuse.Indexing;

// The build-capture worker: a standalone process the main Fuse process spawns for N4 tier-1, so the
// Basic.CompilerLog Roslyn closure never shares a process with the parent's MSBuildWorkspace.
//
//   fuse-build-capture --build <solutionOrProject>   run the build, rehydrate, emit JSON
//   fuse-build-capture --binlog <path>               rehydrate an existing binary log, emit JSON
//
// Output is a single JSON object on stdout: { "succeeded": bool, "reason": string?, "projects": [...] }.
// Exit code 0 on a successful capture, 1 otherwise; diagnostics go to stderr so stdout stays pure JSON.

var rehydrator = new BuildCaptureRehydrator();

// --check <target> <relativeFilePath> <newContentFile>: speculative typecheck of a proposed single-file patch.
// The new content is passed via a file (not an argument) so it is never a length-bounded command-line value.
if (args.Length == 4 && args[0] == "--check")
{
    CheckResult check;
    try
    {
        var newContent = await File.ReadAllTextAsync(args[3]);
        check = await rehydrator.CheckAsync(args[1], args[2], newContent, TimeSpan.FromMinutes(10), CancellationToken.None);
    }
    catch (Exception ex)
    {
        check = CheckResult.Abstain($"check error: {ex.Message}");
    }

    Console.Out.WriteLine(JsonSerializer.Serialize(check, BuildCaptureJsonContext.Default.CheckResult));
    return check.Verified ? 0 : 1;
}

// --check-complog <complogPath> <relativeFilePath> <newContentFile>: speculative typecheck of a proposed
// single-file patch against a captured compiler log, WITHOUT building (C2). The oracle-grade check answer on a
// machine that cannot restore or build; the compilation is rehydrated from the bundle's portable compiler log.
if (args.Length == 4 && args[0] == "--check-complog")
{
    CheckResult check;
    try
    {
        var newContent = await File.ReadAllTextAsync(args[3]);
        check = rehydrator.CheckFromLog(args[1], args[2], newContent, CancellationToken.None);
    }
    catch (Exception ex)
    {
        check = CheckResult.Abstain($"check-complog error: {ex.Message}");
    }

    Console.Out.WriteLine(JsonSerializer.Serialize(check, BuildCaptureJsonContext.Default.CheckResult));
    return check.Verified ? 0 : 1;
}

// --merge <fragmentsDir> <complogOutDir>: convert per-project fragment binlogs to portable compiler logs
// (fail-closed secret scanned) and emit the merged extracted graph as JSON (the G4 fragment-merge channel).
if (args.Length == 3 && args[0] == "--merge")
{
    CaptureResult merged;
    try
    {
        merged = rehydrator.MergeFragmentsToBundle(args[1], args[2], CancellationToken.None);
    }
    catch (Exception ex)
    {
        merged = CaptureResult.Failed($"merge error: {ex.Message}");
    }

    Console.Out.WriteLine(JsonSerializer.Serialize(merged, BuildCaptureJsonContext.Default.CaptureResult));
    return merged.Succeeded ? 0 : 1;
}

// --capture-bundle <target> <complogOut>: build the target and export a portable compiler log (the C2 capture
// artifact) to <complogOut>, emitting the extracted graph as JSON on stdout so the parent can package both.
if (args.Length == 3 && args[0] == "--capture-bundle")
{
    CaptureResult bundle;
    try
    {
        bundle = await rehydrator.ExportCompilerLogAsync(args[1], args[2], TimeSpan.FromMinutes(10), CancellationToken.None);
    }
    catch (Exception ex)
    {
        bundle = CaptureResult.Failed($"capture-bundle error: {ex.Message}");
    }

    Console.Out.WriteLine(JsonSerializer.Serialize(bundle, BuildCaptureJsonContext.Default.CaptureResult));
    return bundle.Succeeded ? 0 : 1;
}

if (args.Length < 2 || args[0] is not ("--build" or "--binlog"))
{
    await Console.Error.WriteLineAsync("usage: fuse-build-capture (--build <target> | --binlog <path> | --capture-bundle <target> <complogOut> | --merge <fragmentsDir> <complogOutDir> | --check <target> <file> <newContentFile> | --check-complog <complogPath> <file> <newContentFile>)");
    return 2;
}

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
