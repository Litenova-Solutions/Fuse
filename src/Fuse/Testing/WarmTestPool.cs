using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Fuse.Changes;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Testing.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;

namespace Fuse.Testing;

/// <summary>
///     Manages a bounded pool of 1 to 2 warm <see cref="WarmTestWorker"/> processes per <c>(Project, TFM)</c> slot,
///     driving Tier 2 <c>Compilation.EmitDifference</c> in-place updates, Tier 1 collectible ALC reloads,
///     <see cref="SliceTestCache"/> lookups, <see cref="DynamicTestMap"/> coverage recording, and automatic worker
///     recycling and fallback on timeouts, crashes or unsupported runner features.
/// </summary>
internal sealed class WarmTestPool : IDisposable, IAsyncDisposable
{
    internal const int DefaultMaxWorkersPerSlot = 2;
    internal const int DefaultMaxTotalWorkers = 8;

    private static readonly Guid EncLocalSlotMapGuid = new("755F52A8-91C5-45BE-B4B8-209571E552BD");
    private static readonly Guid EncLambdaAndClosureMapGuid = new("A643004C-0240-496F-A783-30D64F4979DE");
    private static readonly Guid EncStateMachineStateMapGuid = new("54FD2AC5-E925-401A-9C2A-F94F171072F8");

    private readonly RepoRoot _root;
    private readonly SliceTestCache? _sliceCache;
    private readonly DynamicTestMap? _dynamicMap;
    private readonly Action<string> _log;
    private readonly int _maxWorkersPerSlot;
    private readonly int _maxTotalWorkers;
    private readonly Func<string, string, string?, Process?>? _workerLauncher;
    private readonly ConcurrentDictionary<SlotKey, WorkerSlot> _slots = new();
    private readonly Lock _poolGate = new();
    private bool _disposed;

    public WarmTestPool(
        RepoRoot root,
        SliceTestCache? sliceCache = null,
        DynamicTestMap? dynamicMap = null,
        Action<string>? log = null,
        int maxWorkersPerSlot = DefaultMaxWorkersPerSlot,
        int maxTotalWorkers = DefaultMaxTotalWorkers)
        : this(root, sliceCache, dynamicMap, log, maxWorkersPerSlot, maxTotalWorkers, null)
    {
    }

    internal WarmTestPool(
        RepoRoot root,
        SliceTestCache? sliceCache,
        DynamicTestMap? dynamicMap,
        Action<string>? log,
        int maxWorkersPerSlot,
        int maxTotalWorkers,
        Func<string, string, string?, Process?>? workerLauncher)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = root;
        _sliceCache = sliceCache;
        _dynamicMap = dynamicMap;
        _log = log ?? (_ => { });
        _maxWorkersPerSlot = Math.Clamp(maxWorkersPerSlot, 1, 2);
        _maxTotalWorkers = Math.Max(_maxWorkersPerSlot, maxTotalWorkers);
        _workerLauncher = workerLauncher;
    }

    /// <summary>How many warm worker processes are currently alive in the pool.</summary>
    public int LiveWorkers
    {
        get
        {
            lock (_poolGate)
                return _slots.Values.Sum(s => s.Workers.Count(w => w.IsAlive));
        }
    }

    /// <summary>
    ///     Runs tests for <paramref name="request"/> using <see cref="SliceTestCache"/> first, then a pooled warm
    ///     worker (trying Tier 2 EnC when <paramref name="closureCompilations"/> permits, else Tier 1 collectible ALC),
    ///     recording coverage and slice cache entries on completion.
    /// </summary>
    public async Task<WarmRunResult> RunAsync(
        WarmRunRequest request,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, Compilation>? closureCompilations = null,
        Action<SliceTestFailure>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.NoCache && request.SliceKey is not null && _sliceCache?.TryGet(request.SliceKey, noCache: false) is { } cached)
        {
            return new WarmRunResult(
                ExecutedInWarmWorker: true,
                FromCache: true,
                EncApplied: false,
                StoppedEarly: false,
                cached.Passed,
                cached.Failed,
                cached.Skipped,
                0,
                null,
                cached.Failures);
        }

        if (IsUnsupportedTargetFramework(request.TargetFramework))
            return WarmRunResult.Fallback($"unsupported target framework '{request.TargetFramework}' for warm worker");

        var slotKey = new SlotKey(request.ProjectPath, request.TargetFramework, request.BinDirectory);
        var slot = _slots.GetOrAdd(slotKey, static _ => new WorkerSlot());
        PooledWorker? worker = null;

        try
        {
            worker = await AcquireWorkerAsync(slotKey, slot, request, cancellationToken).ConfigureAwait(false);
            if (worker is null)
                return WarmRunResult.Fallback("no warm worker slot available");

            var runId = Guid.NewGuid().ToString("N")[..12];
            var pendingEnc = TryPrepareEncDeltas(worker, request, closureCompilations, cancellationToken);

            WarmTestWorker.WorkerCommand command;
            if (pendingEnc is not null)
            {
                command = BuildRunCommand(runId, request, assemblies: null, encUpdates: pendingEnc.Select(p => p.Delta).ToArray());
            }
            else
            {
                command = BuildRunCommand(runId, request, ToPayloads(request.Assemblies), encUpdates: null);
            }

            var response = await ExecuteOnWorkerAsync(worker, command, request.Timeout, onFailure, cancellationToken).ConfigureAwait(false);

            if (response.NeedsTier1Reload && pendingEnc is not null)
            {
                worker.ClearBaselines();
                var tier1Command = BuildRunCommand(Guid.NewGuid().ToString("N")[..12], request, ToPayloads(request.Assemblies), encUpdates: null);
                response = await ExecuteOnWorkerAsync(worker, tier1Command, request.Timeout, onFailure, cancellationToken).ConfigureAwait(false);
                pendingEnc = null;
            }

            if (response.RecycleWorker)
            {
                RecycleWorker(slot, worker);
                worker = null;
                return WarmRunResult.Fallback(response.FallbackReason ?? "warm worker recycled", response.TotalMs);
            }

            if (response.FallbackReason is not null)
            {
                worker.ClearBaselines();
                return WarmRunResult.Fallback(response.FallbackReason, response.TotalMs);
            }

            if (pendingEnc is not null && response.EncApplied)
            {
                foreach (var updated in pendingEnc)
                    worker.UpdateBaseline(updated.SimpleName, updated.NextCompilation, updated.NextBaseline);
            }
            else if (request.KeepAliveForEnc && closureCompilations is not null)
            {
                InitializeBaselines(worker, request.Assemblies, closureCompilations);
            }
            else if (!request.KeepAliveForEnc)
            {
                worker.ClearBaselines();
            }

            if (request.CollectCoverage && response.Coverage.Count > 0)
            {
                var relativeProject = _root.Contains(request.ProjectPath) ? _root.PathOf(request.ProjectPath).Relative : request.ProjectPath;
                _dynamicMap?.RecordRunCoverage(relativeProject, _root, response.Coverage);
            }

            if (request.SliceKey is not null)
            {
                var relativeProject = _root.Contains(request.ProjectPath) ? _root.PathOf(request.ProjectPath).Relative : request.ProjectPath;
                _sliceCache?.Store(new SliceCacheResult(
                    request.SliceKey,
                    relativeProject,
                    request.ProjectName,
                    request.TargetFramework,
                    response.Passed,
                    response.Failed,
                    response.Skipped,
                    response.TotalMs,
                    DateTime.UtcNow,
                    response.Failures));
            }

            return new WarmRunResult(
                ExecutedInWarmWorker: true,
                FromCache: false,
                response.EncApplied,
                response.StoppedEarly,
                response.Passed,
                response.Failed,
                response.Skipped,
                response.TotalMs,
                null,
                response.Failures);
        }
        finally
        {
            if (worker is not null)
            {
                lock (_poolGate)
                {
                    worker.Busy = false;
                    worker.LastUsedUtc = DateTime.UtcNow;
                }
            }
        }
    }

    /// <summary>Asks an idle or newly acquired worker to unload its active collectible ALC and returns whether it unloaded cleanly.</summary>
    internal async Task<bool> UnloadSlotAsync(string projectPath, string targetFramework, string binDirectory, CancellationToken cancellationToken)
    {
        var key = new SlotKey(projectPath, targetFramework, binDirectory);
        if (!_slots.TryGetValue(key, out var slot))
            return true;

        List<PooledWorker> snapshot;
        lock (_poolGate)
            snapshot = [.. slot.Workers.Where(w => !w.Busy && w.IsAlive)];

        var allUnloaded = true;
        foreach (var worker in snapshot)
        {
            lock (_poolGate)
            {
                if (worker.Busy)
                    continue;
                worker.Busy = true;
            }

            try
            {
                worker.ClearBaselines();
                var json = JsonSerializer.Serialize(new WarmTestWorker.WorkerCommand { Kind = "unload" }, WarmTestWorker.WarmWorkerJson.Default.WorkerCommand);
                await worker.WriteLineAsync(json, cancellationToken).ConfigureAwait(false);
                var replyLine = await worker.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (replyLine is null)
                {
                    allUnloaded = false;
                    RecycleWorker(slot, worker);
                    continue;
                }

                var evt = JsonSerializer.Deserialize(replyLine, WarmTestWorker.WarmWorkerJson.Default.WorkerEvent);
                allUnloaded &= evt is { Kind: "unloaded", AlcUnloaded: true };
            }
            catch (Exception)
            {
                allUnloaded = false;
                RecycleWorker(slot, worker);
            }
            finally
            {
                lock (_poolGate)
                    worker.Busy = false;
            }
        }

        return allUnloaded;
    }

    /// <summary>Stops and disposes every warm worker process in the pool.</summary>
    public void Dispose()
    {
        List<PooledWorker> all;
        lock (_poolGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            all = [.. _slots.Values.SelectMany(s => s.Workers)];
            _slots.Clear();
        }

        foreach (var worker in all)
            worker.Dispose();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<PooledWorker?> AcquireWorkerAsync(SlotKey slotKey, WorkerSlot slot, WarmRunRequest request, CancellationToken cancellationToken)
    {
        var depsStamp = DependencyStampOf(request.TestAssemblyPath, request.BinDirectory);
        PooledWorker? existing = null;
        List<PooledWorker> staleToDispose = [];
        PooledWorker? evicted = null;

        lock (_poolGate)
        {
            if (_disposed)
                return null;

            for (var i = slot.Workers.Count - 1; i >= 0; i--)
            {
                var candidate = slot.Workers[i];
                if (!candidate.IsAlive || candidate.DepsStamp != depsStamp)
                {
                    if (!candidate.Busy)
                    {
                        slot.Workers.RemoveAt(i);
                        staleToDispose.Add(candidate);
                    }

                    continue;
                }

                if (!candidate.Busy)
                {
                    candidate.Busy = true;
                    existing = candidate;
                    break;
                }
            }

            if (existing is null)
            {
                if (slot.Workers.Count >= _maxWorkersPerSlot)
                    return null;

                var totalLive = _slots.Values.Sum(s => s.Workers.Count(w => w.IsAlive));
                if (totalLive >= _maxTotalWorkers)
                    evicted = EvictOldestIdleLocked();
            }
        }

        foreach (var stale in staleToDispose)
            stale.Dispose();
        evicted?.Dispose();

        if (existing is not null)
            return existing;

        var launched = await LaunchWorkerAsync(slotKey, request, depsStamp, cancellationToken).ConfigureAwait(false);
        if (launched is null)
            return null;

        lock (_poolGate)
        {
            if (_disposed)
            {
                launched.Dispose();
                return null;
            }

            launched.Busy = true;
            slot.Workers.Add(launched);
        }

        return launched;
    }

    private PooledWorker? EvictOldestIdleLocked()
    {
        PooledWorker? oldest = null;
        WorkerSlot? oldestSlot = null;
        foreach (var slot in _slots.Values)
        {
            foreach (var worker in slot.Workers)
            {
                if (worker.Busy)
                    continue;
                if (oldest is null || worker.LastUsedUtc < oldest.LastUsedUtc)
                {
                    oldest = worker;
                    oldestSlot = slot;
                }
            }
        }

        if (oldest is not null && oldestSlot is not null)
            oldestSlot.Workers.Remove(oldest);
        return oldest;
    }

    private void RecycleWorker(WorkerSlot slot, PooledWorker worker)
    {
        lock (_poolGate)
            slot.Workers.Remove(worker);
        worker.Dispose();
    }

    private async Task<PooledWorker?> LaunchWorkerAsync(SlotKey key, WarmRunRequest request, string depsStamp, CancellationToken cancellationToken)
    {
        var pipeName = "fuse-tw-" + Guid.NewGuid().ToString("N")[..16];
        var serverPipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var workerDir = AppContext.BaseDirectory;
        var assemblyFileName = (typeof(WarmTestWorker).Assembly.GetName().Name ?? "fuse") + ".dll";
        var workerAssembly = Path.Combine(workerDir, assemblyFileName);
        if (!File.Exists(workerAssembly))
            workerAssembly = Path.Combine(workerDir, "Fuse.dll");
        if (_workerLauncher is null && !File.Exists(workerAssembly))
        {
            await serverPipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        var psi = new ProcessStartInfo(ResolveDotnetHost())
        {
            WorkingDirectory = Directory.Exists(request.BinDirectory) ? request.BinDirectory : _root.Path,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("exec");
        AddRuntimeConfigArguments(psi.ArgumentList, workerAssembly, workerDir);
        psi.ArgumentList.Add(workerAssembly);
        psi.ArgumentList.Add("test-worker");
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add("--bin");
        psi.ArgumentList.Add(request.BinDirectory);
        if (!string.IsNullOrEmpty(request.TestAssemblyPath))
        {
            psi.ArgumentList.Add("--test-assembly");
            psi.ArgumentList.Add(request.TestAssemblyPath);
        }

        psi.ArgumentList.Add("--repo-root");
        psi.ArgumentList.Add(_root.Path);
        psi.ArgumentList.Add("--parent-pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Enable runtime Edit and Continue (MetadataUpdater.IsSupported) inside the warm worker process.
        psi.Environment["DOTNET_MODIFIABLE_ASSEMBLIES"] = "debug";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_GCConserveMemory"] = "5";

        Process? process = null;
        try
        {
            process = _workerLauncher is not null
                ? _workerLauncher(pipeName, request.BinDirectory, request.TestAssemblyPath)
                : Process.Start(psi);
            if (process is null)
            {
                await serverPipe.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            if (_workerLauncher is null)
            {
                process.StandardInput.Close();
                process.OutputDataReceived += (_, _) => { };
                process.ErrorDataReceived += (_, _) => { };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await serverPipe.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);

            var worker = new PooledWorker(process, serverPipe, depsStamp);
            var helloLine = await worker.ReadLineAsync(connectTimeout.Token).ConfigureAwait(false);
            if (helloLine is null)
            {
                worker.Dispose();
                return null;
            }

            var hello = JsonSerializer.Deserialize(helloLine, WarmTestWorker.WarmWorkerJson.Default.WorkerEvent);
            if (hello is not { Kind: "hello" })
            {
                worker.Dispose();
                return null;
            }

            worker.SupportsMetadataUpdate = hello.SupportsMetadataUpdate;
            _log($"warm test worker started for {Path.GetFileNameWithoutExtension(key.ProjectPath)} ({key.TargetFramework}, pid {hello.ProcessId}, enc={hello.SupportsMetadataUpdate})");
            return worker;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log($"warm test worker failed to start for {Path.GetFileNameWithoutExtension(key.ProjectPath)}: {e.Message}");
            await serverPipe.DisposeAsync().ConfigureAwait(false);
            try
            {
                process?.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }

            process?.Dispose();
            return null;
        }
    }

    private static async Task<WorkerExecutionOutcome> ExecuteOnWorkerAsync(
        PooledWorker worker,
        WarmTestWorker.WorkerCommand command,
        TimeSpan timeout,
        Action<SliceTestFailure>? onFailure,
        CancellationToken cancellationToken)
    {
        using var runTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout > TimeSpan.Zero)
            runTimeout.CancelAfter(timeout);

        var failures = new List<SliceTestFailure>();
        var coverage = new Dictionary<string, DynamicTestMap.CoverageHits>(StringComparer.Ordinal);
        try
        {
            var json = JsonSerializer.Serialize(command, WarmTestWorker.WarmWorkerJson.Default.WorkerCommand);
            await worker.WriteLineAsync(json, runTimeout.Token).ConfigureAwait(false);

            while (true)
            {
                var line = await worker.ReadLineAsync(runTimeout.Token).ConfigureAwait(false);
                if (line is null)
                    return WorkerExecutionOutcome.Recycled("warm worker disconnected unexpectedly");

                WarmTestWorker.WorkerEvent? evt;
                try
                {
                    evt = JsonSerializer.Deserialize(line, WarmTestWorker.WarmWorkerJson.Default.WorkerEvent);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (evt is null || evt.RunId != command.RunId)
                    continue;

                switch (evt.Kind)
                {
                    case "verdict":
                        if (evt.Verdict == "Failed" && evt.TestName is not null)
                        {
                            var failure = new SliceTestFailure(evt.TestName, evt.Message ?? "", evt.Frames ?? []);
                            failures.Add(failure);
                            onFailure?.Invoke(failure);
                        }

                        break;
                    case "coverage":
                        if (!string.IsNullOrEmpty(evt.TestName))
                        {
                            coverage[evt.TestName] = new DynamicTestMap.CoverageHits(
                                evt.CoveredMethods ?? [],
                                evt.CoveredTypes ?? [],
                                []);
                        }

                        break;
                    case "fallback":
                        if (evt.Reason == "enc-reload-needed")
                            return WorkerExecutionOutcome.Tier1Retry();
                        return new WorkerExecutionOutcome(false, false, false, false, 0, 0, 0, 0, evt.Reason ?? "fallback", failures, coverage);
                    case "complete":
                        return new WorkerExecutionOutcome(
                            false,
                            false,
                            evt.EncApplied,
                            evt.StoppedEarly,
                            evt.Passed,
                            evt.Failed,
                            evt.Skipped,
                            evt.TotalMs,
                            null,
                            failures,
                            coverage);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WorkerExecutionOutcome.Recycled($"warm worker timed out after {timeout.TotalSeconds:0.#} s");
        }
        catch (IOException e)
        {
            return WorkerExecutionOutcome.Recycled($"warm worker pipe error: {e.Message}");
        }
    }

    private static List<PendingEncUpdate>? TryPrepareEncDeltas(
        PooledWorker worker,
        WarmRunRequest request,
        IReadOnlyDictionary<string, Compilation>? closureCompilations,
        CancellationToken cancellationToken)
    {
        if (!request.KeepAliveForEnc || !worker.SupportsMetadataUpdate || closureCompilations is null || closureCompilations.Count == 0)
            return null;

        if (!worker.HasAllBaselines(request.Assemblies))
            return null;

        var updates = new List<PendingEncUpdate>();
        foreach (var image in request.Assemblies)
        {
            if (!closureCompilations.TryGetValue(image.SimpleName, out var nextCompilation))
                return null;
            if (!worker.TryGetBaseline(image.SimpleName, out var baselineState))
                return null;

            if (nextCompilation.Options.OptimizationLevel != OptimizationLevel.Debug
                || baselineState.Compilation.Options.OptimizationLevel != OptimizationLevel.Debug)
                return null;

            if (!TryBuildAssemblyDelta(image.SimpleName, baselineState.Compilation, baselineState.Baseline, nextCompilation, cancellationToken, out var pending))
                return null;
            if (pending is not null)
                updates.Add(pending);
        }

        // Only use Tier 2 EnC when at least one delta was emitted (identical compilations can still reuse Tier 1 directly if needed, or return 0-edit EnC).
        return updates.Count > 0 ? updates : null;
    }

    private static bool TryBuildAssemblyDelta(
        string simpleName,
        Compilation oldCompilation,
        EmitBaseline oldBaseline,
        Compilation newCompilation,
        CancellationToken cancellationToken,
        out PendingEncUpdate? pending)
    {
        pending = null;
        var oldTrees = oldCompilation.SyntaxTrees.ToDictionary(t => t.FilePath, StringComparer.OrdinalIgnoreCase);
        var newTrees = newCompilation.SyntaxTrees.ToDictionary(t => t.FilePath, StringComparer.OrdinalIgnoreCase);
        if (oldTrees.Count != newTrees.Count)
            return false;

        var semanticEdits = new List<SemanticEdit>();
        foreach (var (path, newTree) in newTrees)
        {
            if (!oldTrees.TryGetValue(path, out var oldTree))
                return false;
            if (ReferenceEquals(oldTree, newTree))
                continue;

            var oldText = oldTree.GetText(cancellationToken);
            var newText = newTree.GetText(cancellationToken);
            if (oldText.ContentEquals(newText))
                continue;

            var oldRoot = oldTree.GetRoot(cancellationToken);
            var newRoot = newTree.GetRoot(cancellationToken);
            var oldDecls = FileDeclarations.Of(oldRoot);
            var newDecls = FileDeclarations.Of(newRoot);
            if (SurfaceDiff.Compare(oldDecls, newDecls).Changes.Count > 0)
                return false;

            var diffs = CodeDiff.Find(oldRoot, newRoot);
            if (diffs.Count == 0)
                continue;

            var oldModel = oldCompilation.GetSemanticModel(oldTree);
            var newModel = newCompilation.GetSemanticModel(newTree);
            foreach (var diffNode in diffs)
            {
                if (!TryCollectMethodEdits(diffNode, oldDecls, oldModel, newDecls, newModel, semanticEdits, cancellationToken))
                    return false;
            }
        }

        if (semanticEdits.Count == 0)
            return true;

        using var metadataStream = new MemoryStream();
        using var ilStream = new MemoryStream();
        using var pdbStream = new MemoryStream();
        var emitDiff = newCompilation.EmitDifference(oldBaseline, semanticEdits, static _ => false, metadataStream, ilStream, pdbStream, cancellationToken);
        if (!emitDiff.Success || emitDiff.Baseline is null)
            return false;

        pending = new PendingEncUpdate(
            simpleName,
            new WarmTestWorker.WorkerEncDelta
            {
                SimpleName = simpleName,
                MetadataBase64 = Convert.ToBase64String(metadataStream.ToArray()),
                IlBase64 = Convert.ToBase64String(ilStream.ToArray()),
                PdbBase64 = Convert.ToBase64String(pdbStream.ToArray()),
            },
            newCompilation,
            emitDiff.Baseline);
        return true;
    }

    private static bool TryCollectMethodEdits(
        SyntaxNode newNode,
        FileDeclarations oldDecls,
        SemanticModel oldModel,
        FileDeclarations newDecls,
        SemanticModel newModel,
        List<SemanticEdit> edits,
        CancellationToken cancellationToken)
    {
        // Only body-editable members (method, constructor, operator, conversion, property/indexer accessors) qualify for EnC.
        if (newNode is not (MethodDeclarationSyntax or ConstructorDeclarationSyntax or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax))
            return false;

        var newSymbol = newModel.GetDeclaredSymbol(newNode, cancellationToken);
        if (newSymbol is null)
            return false;

        var docId = newSymbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(docId))
            return false;

        var oldSymbol = FindMatchingOldSymbol(docId, oldModel, oldDecls, cancellationToken);
        if (oldSymbol is null)
            return false;

        var oldNode = oldSymbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken);
        if (oldNode is null || !string.Equals(FlatParameters(newNode), FlatParameters(oldNode), StringComparison.Ordinal))
            return false;

        if (newSymbol is IPropertySymbol newProp && oldSymbol is IPropertySymbol oldProp)
        {
            if (newProp.GetMethod is not null && oldProp.GetMethod is not null)
                edits.Add(new SemanticEdit(SemanticEditKind.Update, oldProp.GetMethod, newProp.GetMethod));
            if (newProp.SetMethod is not null && oldProp.SetMethod is not null)
                edits.Add(new SemanticEdit(SemanticEditKind.Update, oldProp.SetMethod, newProp.SetMethod));
            _ = newDecls;
            return newProp.GetMethod is not null || newProp.SetMethod is not null;
        }

        edits.Add(new SemanticEdit(SemanticEditKind.Update, oldSymbol, newSymbol));
        return true;
    }

    private static ISymbol? FindMatchingOldSymbol(string docId, SemanticModel oldModel, FileDeclarations oldDecls, CancellationToken cancellationToken)
    {
        foreach (var decl in oldDecls.All)
        {
            foreach (var part in decl.Parts)
            {
                if (oldModel.GetDeclaredSymbol(part, cancellationToken) is { } sym && sym.GetDocumentationCommentId() == docId)
                    return sym;
            }
        }

        return null;
    }

    private static string FlatParameters(SyntaxNode node) => FileDeclarations.FlatParameters(node);

    private static void InitializeBaselines(
        PooledWorker worker,
        IReadOnlyList<WarmAssemblyImage> images,
        IReadOnlyDictionary<string, Compilation> compilations)
    {
        worker.ClearBaselines();
        foreach (var image in images)
        {
            if (image.Pdb.Length == 0 || !compilations.TryGetValue(image.SimpleName, out var compilation))
                continue;
            if (compilation.Options.OptimizationLevel != OptimizationLevel.Debug)
                continue;
            try
            {
                var peImmutable = ImmutableArray.Create(image.Pe);
                var pdbImmutable = ImmutableArray.Create(image.Pdb);
                var moduleMetadata = ModuleMetadata.CreateFromImage(peImmutable);
                using var peReader = new PEReader(peImmutable);
                using var pdbProvider = MetadataReaderProvider.FromPortablePdbImage(pdbImmutable);
                var peMeta = peReader.GetMetadataReader();
                var pdbMeta = pdbProvider.GetMetadataReader();

                var localSigs = new Dictionary<MethodDefinitionHandle, StandaloneSignatureHandle>();
                var debugInfos = new Dictionary<MethodDefinitionHandle, EditAndContinueMethodDebugInformation>();

                foreach (var handle in peMeta.MethodDefinitions)
                {
                    var methodDef = peMeta.GetMethodDefinition(handle);
                    if (methodDef.RelativeVirtualAddress != 0)
                    {
                        var body = peReader.GetMethodBody(methodDef.RelativeVirtualAddress);
                        localSigs[handle] = body.LocalSignature;
                    }

                    var slotMap = ReadCustomDebugBlob(pdbMeta, handle, EncLocalSlotMapGuid);
                    var lambdaMap = ReadCustomDebugBlob(pdbMeta, handle, EncLambdaAndClosureMapGuid);
                    var stateMap = ReadCustomDebugBlob(pdbMeta, handle, EncStateMachineStateMapGuid);
                    if (!slotMap.IsDefaultOrEmpty || !lambdaMap.IsDefaultOrEmpty || !stateMap.IsDefaultOrEmpty)
                        debugInfos[handle] = EditAndContinueMethodDebugInformation.Create(slotMap, lambdaMap, stateMap);
                }

                var initial = EmitBaseline.CreateInitialBaseline(
                    compilation,
                    moduleMetadata,
                    h => debugInfos.GetValueOrDefault(h),
                    h => localSigs.GetValueOrDefault(h),
                    hasPortableDebugInformation: true);
                worker.SetBaseline(image.SimpleName, compilation, initial, moduleMetadata);
            }
            catch (Exception e) when (e is BadImageFormatException or InvalidDataException or ArgumentException or InvalidOperationException)
            {
            }
        }
    }

    private static ImmutableArray<byte> ReadCustomDebugBlob(MetadataReader pdbReader, MethodDefinitionHandle method, Guid kind)
    {
        foreach (var cdiHandle in pdbReader.GetCustomDebugInformation(method))
        {
            var cdi = pdbReader.GetCustomDebugInformation(cdiHandle);
            if (pdbReader.GetGuid(cdi.Kind) == kind)
                return ImmutableArray.Create(pdbReader.GetBlobBytes(cdi.Value));
        }

        return ImmutableArray<byte>.Empty;
    }

    private WarmTestWorker.WorkerCommand BuildRunCommand(
        string runId,
        WarmRunRequest request,
        WarmTestWorker.WorkerAssemblyPayload[]? assemblies,
        WarmTestWorker.WorkerEncDelta[]? encUpdates) => new()
        {
            Kind = "run",
            RunId = runId,
            BinDirectory = request.BinDirectory,
            WorkingDirectory = request.WorkingDirectory,
            TestAssemblyPath = request.TestAssemblyPath,
            TestAssemblySimpleName = request.TestAssemblySimpleName,
            RepoRoot = _root.Path,
            Assemblies = assemblies,
            EncUpdates = encUpdates,
            SelectedPatterns = [.. request.SelectedPatterns],
            MaxFailures = request.MaxFailures,
            CollectCoverage = request.CollectCoverage,
            KeepAliveForEnc = request.KeepAliveForEnc,
        };

    private static void AddRuntimeConfigArguments(System.Collections.ObjectModel.Collection<string> args, string workerAssembly, string workerDir)
    {
        var ownRuntimeConfig = Path.ChangeExtension(workerAssembly, ".runtimeconfig.json");
        if (File.Exists(ownRuntimeConfig))
            return;

        if (!Directory.Exists(workerDir))
            return;

        var fallbackConfig = Directory.EnumerateFiles(workerDir, "*.runtimeconfig.json").FirstOrDefault();
        if (fallbackConfig is null)
            return;

        args.Add("--runtimeconfig");
        args.Add(fallbackConfig);
        var fallbackDeps = fallbackConfig[..^".runtimeconfig.json".Length] + ".deps.json";
        if (File.Exists(fallbackDeps))
        {
            args.Add("--depsfile");
            args.Add(fallbackDeps);
        }
    }

    private static WarmTestWorker.WorkerAssemblyPayload[] ToPayloads(IReadOnlyList<WarmAssemblyImage> assemblies) =>
    [
        .. assemblies.Select(a => new WarmTestWorker.WorkerAssemblyPayload
        {
            SimpleName = a.SimpleName,
            PeBase64 = Convert.ToBase64String(a.Pe),
            PdbBase64 = Convert.ToBase64String(a.Pdb),
        }),
    ];

    private static bool IsUnsupportedTargetFramework(string targetFramework) =>
        targetFramework.StartsWith("net4", StringComparison.OrdinalIgnoreCase)
        || targetFramework.StartsWith("net3", StringComparison.OrdinalIgnoreCase)
        || targetFramework.StartsWith("net2", StringComparison.OrdinalIgnoreCase);

    private static string DependencyStampOf(string testAssemblyPath, string binDirectory)
    {
        try
        {
            var deps = string.IsNullOrEmpty(testAssemblyPath) ? "" : Path.ChangeExtension(testAssemblyPath, ".deps.json");
            if (!string.IsNullOrEmpty(deps) && File.Exists(deps))
                return File.GetLastWriteTimeUtc(deps).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var firstDeps = Directory.Exists(binDirectory) ? Directory.EnumerateFiles(binDirectory, "*.deps.json").FirstOrDefault() : null;
            if (firstDeps is not null)
                return File.GetLastWriteTimeUtc(firstDeps).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return "0";
    }

    private static string ResolveDotnetHost()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return processPath;

        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var runtime = RuntimeEnvironment.GetRuntimeDirectory();
        var host = Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", name));
        return File.Exists(host) ? host : name;
    }

    private readonly record struct SlotKey(string ProjectPath, string TargetFramework, string BinDirectory);

    private sealed class WorkerSlot
    {
        public List<PooledWorker> Workers { get; } = [];
    }

    private sealed record WorkerBaselineState(Compilation Compilation, EmitBaseline Baseline, ModuleMetadata Metadata);

    private sealed record PendingEncUpdate(
        string SimpleName,
        WarmTestWorker.WorkerEncDelta Delta,
        Compilation NextCompilation,
        EmitBaseline NextBaseline);

    private sealed record WorkerExecutionOutcome(
        bool NeedsTier1Reload,
        bool RecycleWorker,
        bool EncApplied,
        bool StoppedEarly,
        int Passed,
        int Failed,
        int Skipped,
        double TotalMs,
        string? FallbackReason,
        IReadOnlyList<SliceTestFailure> Failures,
        Dictionary<string, DynamicTestMap.CoverageHits> Coverage)
    {
        public static WorkerExecutionOutcome Tier1Retry() =>
            new(true, false, false, false, 0, 0, 0, 0, null, [], new Dictionary<string, DynamicTestMap.CoverageHits>());

        public static WorkerExecutionOutcome Recycled(string reason) =>
            new(false, true, false, false, 0, 0, 0, 0, reason, [], new Dictionary<string, DynamicTestMap.CoverageHits>());
    }

    private sealed class PooledWorker : IDisposable
    {
        private readonly Process _process;
        private readonly NamedPipeServerStream _pipe;
        private readonly StreamReader _reader;
        private readonly Dictionary<string, WorkerBaselineState> _baselines = new(StringComparer.OrdinalIgnoreCase);

        public PooledWorker(Process process, NamedPipeServerStream pipe, string depsStamp)
        {
            _process = process;
            _pipe = pipe;
            _reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            DepsStamp = depsStamp;
            LastUsedUtc = DateTime.UtcNow;
        }

        public string DepsStamp { get; }

        public bool Busy { get; set; }

        public bool SupportsMetadataUpdate { get; set; }

        public DateTime LastUsedUtc { get; set; }

        public bool IsAlive
        {
            get
            {
                try
                {
                    return !_process.HasExited && _pipe.IsConnected;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }

        public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await _pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            _reader.ReadLineAsync(cancellationToken).AsTask();

        public bool HasAllBaselines(IReadOnlyList<WarmAssemblyImage> assemblies) =>
            assemblies.Count > 0 && assemblies.All(a => _baselines.ContainsKey(a.SimpleName));

        public bool TryGetBaseline(string simpleName, out WorkerBaselineState state) =>
            _baselines.TryGetValue(simpleName, out state!);

        public void SetBaseline(string simpleName, Compilation compilation, EmitBaseline baseline, ModuleMetadata metadata)
        {
            if (_baselines.TryGetValue(simpleName, out var old))
                old.Metadata.Dispose();
            _baselines[simpleName] = new WorkerBaselineState(compilation, baseline, metadata);
        }

        public void UpdateBaseline(string simpleName, Compilation compilation, EmitBaseline baseline)
        {
            if (_baselines.TryGetValue(simpleName, out var old))
                _baselines[simpleName] = new WorkerBaselineState(compilation, baseline, old.Metadata);
        }

        public void ClearBaselines()
        {
            foreach (var state in _baselines.Values)
                state.Metadata.Dispose();
            _baselines.Clear();
        }

        public void Dispose()
        {
            ClearBaselines();
            try
            {
                if (_pipe.IsConnected)
                {
                    var bytes = Encoding.UTF8.GetBytes("{\"kind\":\"shutdown\"}\n");
                    _pipe.Write(bytes, 0, bytes.Length);
                    _pipe.Flush();
                }
            }
            catch (Exception)
            {
            }

            _reader.Dispose();
            _pipe.Dispose();
            try
            {
                if (!_process.HasExited)
                {
                    if (!_process.WaitForExit(1000))
                        _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
            }

            _process.Dispose();
        }
    }
}
