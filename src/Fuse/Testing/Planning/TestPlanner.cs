using System.Diagnostics;
using System.Text;
using Fuse.Dotnet.Processes;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Telemetry;
using Fuse.Testing.Caching;
using Fuse.Testing.Filtering;
using Fuse.Testing.Model;
using Fuse.Testing.Model.Caching;
using Fuse.Testing.Selection;
using Fuse.Testing.Shadow;
using Fuse.Workspace.Generations;
using Microsoft.CodeAnalysis;
namespace Fuse.Testing.Planning;

/// <summary>
///     Turns the working-tree changes into a <see cref="TestPlanResult"/>: which test projects to run, which tests in
///     each (<see cref="TestSelector"/>, <see cref="TestFilter"/>), and whether each can be a shadow run
///     (<see cref="ShadowEmitter"/>). It records the sync, the selection, the mirror and the emit as phases.
/// </summary>
/// <remarks>
///     A plan has two parts. <see cref="SelectAsync"/> syncs the workspace under its writer lock, pins the
///     generation that sync publishes and selects the tests from that generation alone, so a plan holds no
///     request lock while it selects and blocks no check. <see cref="PrepareAsync"/> mirrors build output and
///     emits the changed assemblies into the shadow folders, against the generation <see cref="SelectAsync"/>
///     pinned, with an emitter of its own, so plans for different sessions prepare at once.
/// </remarks>
internal sealed class TestPlanner
{
    /// <summary>How long one container runtime probe answers plans; starting the runtime is noticed at the next plan past it.</summary>
    private static readonly TimeSpan RuntimeProbeLifetime = TimeSpan.FromMinutes(5);

    private readonly RepoWorkspace _workspace;
    private readonly DynamicTestMap _dynamicMap;
    private readonly TestSelector _selector;
    private readonly TestCounter _counter = new();
    private readonly ShadowEmitCaches _shadow = new();
    private readonly TimeProvider _time;
    private readonly Func<bool> _containerProbe;

    /// <summary>Guards the container runtime probe and the answer it gave, which concurrent plans read together.</summary>
    private readonly Lock _runtimeGate = new();

    private DateTime _runtimeCheckedAt = DateTime.MinValue;
    private bool _runtimePresent;

    /// <param name="time">Measures the container runtime probe against its lifetime; a test moves it past the probe's expiry.</param>
    /// <param name="containerProbe">Probes for a container runtime; a test answers without one.</param>
    public TestPlanner(RepoWorkspace workspace, TimeProvider? time = null, Func<bool>? containerProbe = null)
    {
        _workspace = workspace;
        var sharedState = workspace.Root.SharedStateDirectory;
        _dynamicMap = new DynamicTestMap(sharedState);
        _selector = new TestSelector(workspace, TimeProvider.System, _dynamicMap);
        SliceCache = new SliceTestCache(sharedState);
        Emitter = new ShadowEmitter(workspace, _shadow);
        _time = time ?? TimeProvider.System;
        _containerProbe = containerProbe ?? ContainerRuntime.IsRunning;
    }

    /// <summary>
    ///     The emitter state every plan of this planner shares: the emitted assemblies kept between plans and the
    ///     shadow files they wrote. Each plan prepares with an emitter of its own over this state, and a test reads
    ///     its counts here.
    /// </summary>
    public ShadowEmitter Emitter { get; }

    /// <summary>Content-addressed per-slice test cache shared across plans.</summary>
    internal SliceTestCache SliceCache { get; }

    /// <summary>Persistent dynamic coverage mapping used for selective test resolution.</summary>
    internal DynamicTestMap DynamicMap => _dynamicMap;

    /// <summary>An emitter for one plan, bound to the generation that plan pinned and sharing this planner's emitter state.</summary>
    private ShadowEmitter EmitterFor(Generation generation) => new(_workspace, generation, _shadow);

    /// <summary>Selects the tests and prepares their shadow runs, as one call; the engine calls the two parts itself.</summary>
    /// <param name="scope">The affected tests, or every test.</param>
    /// <param name="phases">Collects how long each phase of this plan took; <see cref="PhaseTimes.None"/> collects nothing.</param>
    /// <param name="cancellationToken">Cancels the plan.</param>
    public async Task<TestPlanResult> PlanAsync(TestScope scope, PhaseTimes phases, CancellationToken cancellationToken)
    {
        var pending = await SelectAsync(scope, phases, cancellationToken).ConfigureAwait(false);
        return await PrepareAsync(pending, phases, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Syncs the workspace and selects the tests in each test project, from the generation that sync pins. The
    ///     sync itself holds the workspace writer lock; the selection reads only the pinned generation, so it
    ///     holds no request lock and a sync that publishes a later generation cannot change its answer.
    /// </summary>
    /// <param name="scope">The affected tests, or every test.</param>
    /// <param name="phases">Collects the <see cref="Phase.Sync"/> and <see cref="Phase.Selection"/> phases.</param>
    /// <param name="cancellationToken">Cancels the plan.</param>
    public async Task<PendingTestPlan> SelectAsync(TestScope scope, PhaseTimes phases, CancellationToken cancellationToken)
    {
        await IngestIncomingTestMapsAsync(cancellationToken).ConfigureAwait(false);
        var syncing = phases.Start();
        // A plan confirms the changed set against git when the watcher reported nothing, as a check of every
        // change does, so a missed write never narrows the selection to fewer tests.
        var generation = await _workspace.SyncAndPinAsync([], cancellationToken, confirmWithDisk: true).ConfigureAwait(false);
        phases.Add(Phase.Sync, syncing);
        var graph = generation.Graph;
        var testProjects = graph.Projects.Where(p => p.IsTest).ToList();
        // Paths the command named limit the plan to the test projects they name; the summary says which.
        var named = scope is TestScope.Affected { NamedPaths.Length: > 0 } namedScope
            ? NamedTestProjects.Resolve(graph, namedScope.NamedPaths)
            : null;
        if (named is not null)
            testProjects = testProjects.Where(named.Contains).ToList();
        var namedNote = named is null ? "" : $"; in {string.Join(", ", testProjects.Select(p => p.Name))}";
        var total = testProjects.Sum(p => _counter.TestsIn(p).Count);
        if (testProjects.Count == 0)
            return PendingTestPlan.Of(new TestPlanResult([], 0, 0, "no test projects in this repository"), generation);

        if (scope is TestScope.All)
        {
            // A project that needs a container runtime runs only when one answers; without one its tests are
            // left out of the plan and the summary names them.
            var plain = testProjects;
            var gatedTests = 0;
            string[] gatedProjects = [];
            if (testProjects.Any(p => p.RequiresContainerRuntime))
            {
                if (ContainerRuntimePresent())
                {
                    _workspace.Log($"test plan: container runtime present; {testProjects.Count(p => p.RequiresContainerRuntime)} gated project(s) planned");
                }
                else
                {
                    plain = testProjects.Where(p => !p.RequiresContainerRuntime).ToList();
                    var gated = testProjects.Where(p => p.RequiresContainerRuntime).Select(p => (p.Name, Tests: _counter.TestsIn(p).Count)).Where(g => g.Tests > 0).ToList();
                    gatedTests = gated.Sum(g => g.Tests);
                    gatedProjects = [.. gated.Select(g => g.Name).Order(StringComparer.Ordinal)];
                    if (gatedTests > 0)
                        _workspace.Log($"test plan: left out {gatedTests} test(s) in {string.Join(", ", gatedProjects)} (no container runtime)");
                }
            }

            var everything = plain.Select(p => new PlannedRun(p.Path, p.Name, new RunMode.Build(), null, RunnerOf(p, RunSettingsOf(p), []))).ToArray();
            var allSummary = $"ran every test in {plain.Count} test project(s)";
            if (gatedTests > 0)
                allSummary += $"; skipped {gatedTests} test(s) in {string.Join(", ", gatedProjects)} (no container runtime)";
            return PendingTestPlan.Of(new TestPlanResult(everything, total - gatedTests, total, allSummary) { ContainerGatedTests = gatedTests, ContainerGatedProjects = gatedProjects }, generation);
        }

        // A plan for one session leaves out the files only other sessions wrote: the tests their changes affect are
        // theirs to run.
        var leftOut = scope is TestScope.Affected affected ? affected.LeftOut : new HashSet<RepoPath>();
        var owned = generation.Changed.Where(p => graph.OwnersOf(p).Count > 0).ToList();
        var allNonSources = generation.NonSources.ToList();
        var changedNonSources = allNonSources.Where(p => !leftOut.Contains(p)).ToList();
        var outside = changedNonSources.Where(p => graph.NonSourceOwnersOf(p).Count == 0 && graph.ContainingProject(p) is null).OrderBy(p => p.Relative, StringComparer.Ordinal).ToList();
        var changedSources = owned.Where(p => !leftOut.Contains(p)).ToList();
        var changed = changedSources.Concat(changedNonSources).ToList();
        var others = (owned.Count - changedSources.Count) + (allNonSources.Count - changedNonSources.Count);
        var othersNote = others > 0 ? $"; left out {others} changed file(s) only other sessions wrote" : "";
        if (changed.Count == 0 && others > 0)
            return PendingTestPlan.Of(new TestPlanResult([], 0, total, $"no C# changes by this session since HEAD, so no test is affected{othersNote}{namedNote} (fuse test --all runs everything)"), generation);
        if (changed.Count == 0)
            return PendingTestPlan.Of(new TestPlanResult([], 0, total, $"no C# changes since HEAD, so no test is affected{namedNote} (fuse test --all runs everything)"), generation);

        var timer = Stopwatch.StartNew();
        var (outcome, loaded) = await _selector.SelectAsync(changed, generation, cancellationToken).ConfigureAwait(false);
        generation = loaded;
        graph = generation.Graph;
        var selections = outcome.Selections;
        phases.Add(Phase.Selection, timer);
        _workspace.Log($"test plan: selection in {timer.ElapsedMilliseconds} ms: {string.Join("; ", selections.Select(s => $"{Path.GetFileNameWithoutExtension(s.Key.Absolute)} {Describe(s.Value)}"))}");
        var projects = new List<PlannedProjectWork>();
        // How dotnet test reaches a platform project decides how xUnit v3 spells its filter options.
        var runner = GlobalJson.UsesTestingPlatform(_workspace.Root.Path, _workspace.Root.Path);
        var selected = 0;
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var collapsed = new List<string>();
        var platformCollapsed = new List<string>();
        var snapshot = generation.Current;
        var buildOnly = scope is TestScope.Affected { BuildOnly: true };
        bool? runtimePresent = null;
        var containerGatedTests = 0;
        var containerGatedProjects = new List<string>();
        foreach (var (path, selection) in selections.OrderBy(s => s.Key.Absolute, StringComparer.Ordinal))
        {
            var node = graph.Find(path);
            if (node is null || (named is not null && !named.Contains(node)))
                continue;
            // A project that needs a container runtime runs only when one answers. Its tests are left out of
            // the plan, never counted as passed, and the summary names them.
            if (node.RequiresContainerRuntime)
            {
                runtimePresent ??= ContainerRuntimePresent();
                if (!runtimePresent.Value)
                {
                    if (selection is TestSelection.Whole gatedWhole)
                        reasons.Add(gatedWhole.Reason);
                    if (selection is not TestSelection.Methods { Patterns.Count: 0 })
                    {
                        var gated = _counter.TestsIn(node);
                        var gatedCount = node.UsesTestingPlatform && node.Framework == TestFramework.Unknown && selection is TestSelection.Methods
                            ? gated.Count
                            : TestCounter.Count(gated, selection);
                        if (gatedCount > 0)
                        {
                            containerGatedTests += gatedCount;
                            containerGatedProjects.Add(node.Name);
                        }
                    }

                    continue;
                }
            }

            var tests = _counter.TestsIn(node);
            // A platform project whose framework Fuse cannot filter runs whole (below), so every test in it counts as run.
            var count = node.UsesTestingPlatform && node.Framework == TestFramework.Unknown && selection is TestSelection.Methods
                ? tests.Count
                : TestCounter.Count(tests, selection);
            // A static count of zero is not proof nothing matches (inherited test methods run under the derived class's name), so only an empty selection is skipped.
            if (selection is TestSelection.Methods { Patterns.Count: 0 })
                continue;
            selected += count;
            if (selection is TestSelection.Whole whole)
                reasons.Add(whole.Reason);

            if (node.UsesTestingPlatform)
            {
                // Keep the framework's platform filter for MSBuild fallback, and also resolve variants so built
                // platform test projects execute via fast shadow runs.
                var (platformFilter, arguments) = PlatformRun(node, selection, runner, collapsed, platformCollapsed, reasons);
                projects.Add(new PlannedProjectWork(node, platformFilter, arguments, RepoWorkspace.ProjectsFor(snapshot, node).ToList()));
                continue;
            }

            var filter = TestFilter.For(selection);
            if (filter.Collapse != FilterCollapse.None)
            {
                var patterns = ((TestSelection.Methods)selection).Patterns.Count;
                var form = filter.Collapse == FilterCollapse.ToClasses ? $"{filter.Patterns} class prefix(es)" : "no filter, so the project runs whole";
                _workspace.Log($"test plan: {node.Name} filter of {patterns} pattern(s) is above {TestFilter.MaxPatterns}, collapsed to {form}");
                NoteCollapse(node, filter.Collapse, collapsed);
            }

            projects.Add(new PlannedProjectWork(node, filter.Expression, [], RepoWorkspace.ProjectsFor(snapshot, node).ToList()));
        }

        containerGatedProjects.Sort(StringComparer.Ordinal);
        if (runtimePresent is true)
            _workspace.Log($"test plan: container runtime present; {projects.Count(p => p.Node.RequiresContainerRuntime)} gated project(s) planned");
        else if (runtimePresent is false && containerGatedTests > 0)
            _workspace.Log($"test plan: left out {containerGatedTests} test(s) in {string.Join(", ", containerGatedProjects)} (no container runtime)");

        if (projects.Count == 0 && outside.Count > 0 && containerGatedTests == 0)
            return PendingTestPlan.Of(new TestPlanResult([], 0, total, OutsideSummary(outside, othersNote, namedNote)), generation);
        if (selections.Count == 0 && outcome.AllSameCode)
            return PendingTestPlan.Of(new TestPlanResult([], 0, total, $"no test is affected by the changes (out of {total}); the changes compile to the same code as HEAD; fuse test --all runs everything"), generation);

        var summary = new StringBuilder($"ran {selected} test(s) affected by your changes out of {total}");
        if (reasons.Count > 0)
            summary.Append(" (whole projects where ").Append(string.Join("; ", reasons)).Append(')');
        if (collapsed.Count > 0)
            summary.Append("; ran ").Append(string.Join(", ", collapsed)).Append(", because a test filter holds at most ").Append(TestFilter.MaxPatterns).Append(" names");
        if (platformCollapsed.Count > 0)
            summary.Append("; ran ").Append(string.Join(", ", platformCollapsed)).Append(", because a platform filter holds at most ").Append(PlatformFilter.MaxNames).Append(" names");
        summary.Append(othersNote);
        summary.Append(namedNote);
        if (containerGatedTests > 0)
            summary.Append($"; skipped {containerGatedTests} test(s) in {string.Join(", ", containerGatedProjects)} (no container runtime)");
        if (outside.Count > 0)
            summary.Append($"; {outside.Count} non-C# file(s) outside every project changed ({OutsideSample(outside)}); a test that reads them by path is not selected");
        summary.Append(outside.Count > 0 ? "; fuse test --all covers them" : "; fuse test --all runs everything");
        return PendingTestPlan.ToPrepare(
            generation,
            EmitterFor(generation),
            projects,
            selected,
            total,
            summary.ToString(),
            scope is TestScope.Affected { BuildOnly: true },
            containerGatedTests,
            [.. containerGatedProjects],
            noCache: scope is TestScope.Affected { NoCache: true },
            runner: runner,
            selections: selections);
    }

    /// <summary>
    ///     Prepares the shadow runs of a plan <see cref="SelectAsync"/> made, from the generation it pinned, and
    ///     answers the plan. It reads only that generation and the plan's own emitter, so plans prepare at the
    ///     same time without a lock between them.
    /// </summary>
    /// <param name="pending">The plan, with its generation.</param>
    /// <param name="phases">Collects the <see cref="Phase.Mirror"/> and <see cref="Phase.Emit"/> phases.</param>
    /// <param name="cancellationToken">Cancels emitting.</param>
    public async Task<TestPlanResult> PrepareAsync(PendingTestPlan pending, PhaseTimes phases, CancellationToken cancellationToken)
    {
        if (pending is PendingTestPlan.Ready ready)
            return ready.Result;

        if (pending is not PendingTestPlan.NeedsPreparation preparation)
            throw new UnreachableException($"a pending plan is answered or needs preparation, not {pending.GetType().Name}");

        var metadata = preparation.Metadata;
        var emitter = preparation.Emitter;
        try
        {
            var preparing = Stopwatch.StartNew();
            var emitting = new Stopwatch();
            var runs = new List<PlannedRun>();
            foreach (var work in preparation.Work)
                runs.AddRange(await RunsOfAsync(emitter, work.Node, work.Filter, work.FilterArguments, work.Variants, preparation, emitting, preparing, cancellationToken).ConfigureAwait(false));

            // Everything in the preparation but the emit is the mirror: judging which assemblies are stale, and the copy.
            phases.Add(Phase.Mirror, preparing.Elapsed.TotalMilliseconds - emitting.Elapsed.TotalMilliseconds);
            phases.Add(Phase.Emit, emitting.Elapsed.TotalMilliseconds);
            if (runs.Count == 0)
            {
                // Every selected test needs a container runtime that is not running; the client answers that no test ran.
                if (metadata.GatedTests > 0)
                {
                    return new TestPlanResult([], 0, metadata.Total, metadata.Summary)
                    {
                        ContainerGatedTests = metadata.GatedTests,
                        ContainerGatedProjects = metadata.GatedProjects,
                    };
                }

                return new TestPlanResult([], 0, metadata.Total, $"no test is affected by the changes (out of {metadata.Total}); fuse test --all runs everything");
            }

            return new TestPlanResult([.. runs], metadata.Selected, metadata.Total, metadata.Summary)
            {
                ContainerGatedTests = metadata.GatedTests,
                ContainerGatedProjects = metadata.GatedProjects,
            };
        }
        finally
        {
            emitter.ReleaseCopies();
        }
    }

    /// <summary>
    ///     The runs of one test project (VSTest or Microsoft.Testing.Platform): a shadow run per target framework when
    ///     every one of them can have one, and otherwise one build with MSBuild for the project, which runs every
    ///     target framework.
    /// </summary>
    private async Task<IReadOnlyList<PlannedRun>> RunsOfAsync(
        ShadowEmitter emitter,
        ProjectNode node,
        string? filter,
        string[] filterArguments,
        IReadOnlyList<Project> variants,
        PendingTestPlan.NeedsPreparation preparation,
        Stopwatch emitting,
        Stopwatch timer,
        CancellationToken cancellationToken)
    {
        var metadata = preparation.Metadata;
        // A configuration or framework was named, or a platform project targets .NET Framework without runtimeconfig:
        // only a build with MSBuild handles it, so no shadow run is prepared.
        if (metadata.BuildOnly || variants.Count == 0 || (node.UsesTestingPlatform && variants.Any(IsLegacyNetFramework)))
        {
            if (node.UsesTestingPlatform)
            {
                var reason = variants.Any(IsLegacyNetFramework) ? "targets .NET Framework without runtimeconfig; builds with MSBuild" : $"builds with MSBuild after {timer.ElapsedMilliseconds} ms";
                _workspace.Log($"test plan: {node.Name} [testing platform] {reason}");
            }

            return [new PlannedRun(node.Path, node.Name, new RunMode.Build(), filter, RunnerOf(node, RunSettingsOf(node), filterArguments))];
        }

        var prepared = new List<(Project Variant, RunMode.Shadow Shadow, string? Filter, string[] FilterArguments, SliceCacheState Cache)>();
        var runSettings = RunSettingsOf(node);
        foreach (var variant in variants)
        {
            var result = await emitter.PrepareDetailedAsync(variant, preparation.Generation.Graph, _workspace.Log, emitting, cancellationToken).ConfigureAwait(false);
            if (result.Mode is not RunMode.Shadow shadow)
            {
                prepared.Clear();
                break;
            }

            if (node.UsesTestingPlatform
                && shadow.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                && !File.Exists(Path.ChangeExtension(shadow.Assembly, ".runtimeconfig.json")))
            {
                _workspace.Log($"test plan: {node.Name} [{variant.Name}] lacks runtimeconfig.json; builds with MSBuild");
                prepared.Clear();
                break;
            }

            var variantFilterArguments = filterArguments;
            if (node.UsesTestingPlatform
                && node.Framework == TestFramework.XUnit
                && metadata.Selections is not null
                && metadata.Selections.TryGetValue(node.Path, out var selection))
            {
                var shadowRunner = await UsesMtpEntryPointAsync(variant, metadata.Runner, cancellationToken).ConfigureAwait(false);
                if (shadowRunner != metadata.Runner)
                    variantFilterArguments = PlatformFilter.For(node.Framework, selection, shadowRunner).Arguments;
            }

            var shadowDir = Path.GetDirectoryName(shadow.Assembly);
            var sliceKey = SliceCache.ComputeKey(variant, preparation.Generation.Graph, _workspace.Root, result.Closure, result.Emitted, shadowDir, runSettings, filter, variantFilterArguments);
            var cached = metadata.NoCache ? null : SliceCache.TryGet(sliceKey, noCache: false);
            prepared.Add((variant, shadow, filter, variantFilterArguments, CacheOf(sliceKey, cached)));
        }

        var tag = node.UsesTestingPlatform ? " [testing platform]" : "";
        _workspace.Log($"test plan: {node.Name}{tag} [{string.Join(", ", variants.Select(v => v.Name))}] {(prepared.Count == 0 ? "builds with MSBuild" : $"runs from {string.Join(", ", prepared.Select(p => p.Shadow.Assembly))}")} after {timer.ElapsedMilliseconds} ms");
        if (prepared.Count == 0)
            return [new PlannedRun(node.Path, node.Name, new RunMode.Build(), filter, RunnerOf(node, runSettings, filterArguments))];

        return
        [
            .. prepared.Select(p => new PlannedRun(
                node.Path,
                node.Name,
                p.Shadow,
                p.Filter,
                RunnerOf(node, runSettings, p.FilterArguments, [p.Shadow.Assembly]))
            {
                Cache = p.Cache,
            }),
        ];
    }

    /// <summary>How a run of <paramref name="node"/> reaches its test host, with its runsettings and platform paths.</summary>
    private static TestRunnerPlan RunnerOf(ProjectNode node, string? runSettings, string[] filterArguments, string[]? targetPaths = null)
    {
        if (node.UsesTestingPlatform)
            return new TestRunnerPlan.TestingPlatform(runSettings, filterArguments, targetPaths ?? [.. node.TargetPaths]);
        return new TestRunnerPlan.VsTest(runSettings);
    }

    /// <summary>What the slice cache holds for a computed <paramref name="sliceKey"/> and its lookup answer.</summary>
    private static SliceCacheState CacheOf(string? sliceKey, SliceCacheResult? cached)
    {
        if (cached is not null && sliceKey is not null)
            return new SliceCacheState.Cached(sliceKey, cached);
        if (sliceKey is not null)
            return new SliceCacheState.Pending(sliceKey);
        return new SliceCacheState.Uncached();
    }

    private static bool IsLegacyNetFramework(Project variant)
    {
        if (variant.OutputFilePath is null)
            return true;
        if (!File.Exists(Path.ChangeExtension(variant.OutputFilePath, ".runtimeconfig.json")))
            return true;
        var outputDir = Path.GetFileName(Path.GetDirectoryName(variant.OutputFilePath));
        return outputDir is not null && IsLegacyNetFrameworkTfm(outputDir);
    }

    private static bool IsLegacyNetFrameworkTfm(string tfm) =>
        tfm.Length >= 4
        && tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase)
        && !tfm.Contains('.')
        && char.IsDigit(tfm[3])
        && tfm[3] <= '4';

    /// <summary>
    ///     Ingests the test maps the client's platform runs left in the incoming folder into the dynamic map, so the
    ///     selection adds the tests they name. Silent when none arrived.
    /// </summary>
    private async Task IngestIncomingTestMapsAsync(CancellationToken cancellationToken)
    {
        var ingested = await _dynamicMap.IngestIncomingAsync(_workspace.Root, cancellationToken).ConfigureAwait(false);
        if (ingested > 0)
            _workspace.Log($"test plan: ingested {ingested} test mapping(s) from earlier platform runs");
    }

    private static async Task<bool> UsesMtpEntryPointAsync(Project variant, bool defaultRunner, CancellationToken cancellationToken)
    {
        foreach (var document in variant.Documents)
        {
            var name = document.Name;
            var filePath = document.FilePath;
            if (!name.Contains("XunitAutoGeneratedEntryPoint", StringComparison.OrdinalIgnoreCase)
                && (filePath is null || !filePath.Contains("XunitAutoGeneratedEntryPoint", StringComparison.OrdinalIgnoreCase)))
                continue;

            var text = document.TryGetText(out var existing)
                ? existing.ToString()
                : (await document.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
            if (text.Contains("ConsoleRunner.Run", StringComparison.Ordinal))
                return false;
            if (text.Contains("TestPlatformTestFramework", StringComparison.Ordinal))
                return true;
        }

        return defaultRunner;
    }

    /// <summary>
    ///     The filter of one project on Microsoft.Testing.Platform: the VSTest expression for MSTest and NUnit, which
    ///     accept it there, the framework's own arguments for xUnit v3 and TUnit, and none for a framework Fuse does
    ///     not know, whose project then runs whole with its reason in <paramref name="reasons"/>. A filter that
    ///     collapsed to fit its bound is logged and noted in the list its bound belongs to.
    /// </summary>
    private (string? Filter, string[] Arguments) PlatformRun(ProjectNode node, TestSelection selection, bool runner, List<string> collapsed, List<string> platformCollapsed, HashSet<string> reasons)
    {
        switch (node.Framework)
        {
            case TestFramework.MSTest or TestFramework.NUnit:
                {
                    var filter = TestFilter.For(selection);
                    if (filter.Collapse != FilterCollapse.None)
                    {
                        var form = filter.Collapse == FilterCollapse.ToClasses ? $"{filter.Patterns} class prefix(es)" : "no filter, so the project runs whole";
                        _workspace.Log($"test plan: {node.Name} filter of {((TestSelection.Methods)selection).Patterns.Count} pattern(s) is above {TestFilter.MaxPatterns}, collapsed to {form}");
                        NoteCollapse(node, filter.Collapse, collapsed);
                    }

                    return (filter.Expression, []);
                }
            case TestFramework.XUnit or TestFramework.TUnit:
                {
                    var (arguments, collapse) = PlatformFilter.For(node.Framework, selection, runner);
                    if (collapse != FilterCollapse.None)
                    {
                        var form = collapse == FilterCollapse.ToClasses ? "whole test classes" : "no filter, so the project runs whole";
                        _workspace.Log($"test plan: {node.Name} filter of {((TestSelection.Methods)selection).Patterns.Count} pattern(s) does not fit {PlatformFilter.MaxNames} names, collapsed to {form}");
                        NoteCollapse(node, collapse, platformCollapsed);
                    }

                    return (null, arguments);
                }
            default:
                if (selection is TestSelection.Methods)
                {
                    _workspace.Log($"test plan: {node.Name} runs whole: its test framework is not one Fuse filters");
                    reasons.Add($"{node.Name} runs on a test framework Fuse cannot filter");
                }

                return (null, []);
        }
    }

    /// <summary>Notes what a collapsed filter ran instead, for the summary, in the list its bound belongs to.</summary>
    private static void NoteCollapse(ProjectNode node, FilterCollapse collapse, List<string> collapsed)
    {
        if (collapse == FilterCollapse.ToClasses)
            collapsed.Add($"whole test classes in {node.Name}");
        else if (collapse == FilterCollapse.ToWholeProject)
            collapsed.Add($"all of {node.Name}");
    }

    /// <summary>
    ///     The runsettings file a run of <paramref name="node"/> carries: its own, except on Microsoft.Testing.Platform
    ///     under a framework that does not read one, which is told none.
    /// </summary>
    private static string? RunSettingsOf(ProjectNode node) =>
        node.UsesTestingPlatform && node.Framework is not (TestFramework.MSTest or TestFramework.NUnit) ? null : node.RunSettings;

    /// <summary>The summary for a plan that selects nothing because every non-C# change is outside every project.</summary>
    private static string OutsideSummary(List<RepoPath> outside, string othersNote, string namedNote) =>
        $"{outside.Count} non-C# file(s) outside every project changed ({OutsideSample(outside)}); a test that reads them by path is not selected{othersNote}{namedNote}; fuse test --all covers them";

    /// <summary>The first outside files by relative path, with the rest elided past three.</summary>
    private static string OutsideSample(List<RepoPath> outside) =>
        string.Join(", ", outside.Take(3).Select(p => p.Relative)) + (outside.Count > 3 ? ", ..." : "");

    /// <summary>A selection as the engine log gives it: "all", or how many patterns it has.</summary>
    private static string Describe(TestSelection selection) =>
        selection is TestSelection.Methods methods ? methods.Patterns.Count + " pattern(s)" : "all";

    /// <summary>True when a container runtime answers, probed at most once every <see cref="RuntimeProbeLifetime"/>.</summary>
    private bool ContainerRuntimePresent()
    {
        lock (_runtimeGate)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            if (now - _runtimeCheckedAt < RuntimeProbeLifetime)
                return _runtimePresent;
            _runtimePresent = _containerProbe();
            _runtimeCheckedAt = now;
            return _runtimePresent;
        }
    }

    /// <summary>
    /// A recorded test name as a filter matches it: a theory row without its argument values.
    /// </summary>
    private static string StripTheoryArgs(string name)
    {
        var args = name.IndexOf('(');
        return args < 0 ? name : name[..args].Trim();
    }

    /// <summary>
    ///     Selects the recorded failures <paramref name="byProject"/> names for a rerun, in the test projects they are
    ///     still in, from the generation its sync pins. Projects that are gone or hold no tests are left out and
    ///     named in the summary, as are the paths <paramref name="unknown"/> holds that name no file.
    /// </summary>
    /// <param name="byProject">Recorded failure names by test project file.</param>
    /// <param name="unknown">Recorded project paths that name no file.</param>
    /// <param name="phases">Collects the <see cref="Phase.Sync"/> and <see cref="Phase.Selection"/> phases.</param>
    /// <param name="cancellationToken">Cancels the plan.</param>
    /// <param name="noCache">True to bypass the slice cache lookup for this plan (<c>--clean</c> / <c>--no-cache</c>).</param>
    public async Task<PendingTestPlan> SelectFailedAsync(IReadOnlyDictionary<RepoPath, string[]> byProject, IReadOnlyList<string> unknown, PhaseTimes phases, CancellationToken cancellationToken, bool noCache = false)
    {
        await IngestIncomingTestMapsAsync(cancellationToken).ConfigureAwait(false);
        var syncing = phases.Start();
        var generation = await _workspace.SyncAndPinAsync([], cancellationToken, confirmWithDisk: true).ConfigureAwait(false);
        phases.Add(Phase.Sync, syncing);
        var graph = generation.Graph;
        var testProjects = graph.Projects.Where(p => p.IsTest).ToList();
        var total = testProjects.Sum(p => _counter.TestsIn(p).Count);
        if (testProjects.Count == 0)
            return PendingTestPlan.Of(new TestPlanResult([], 0, 0, "no test projects in this repository"), generation);

        var timer = Stopwatch.StartNew();
        var leftOut = unknown.Where(p => p.Length > 0).Select(p => Path.GetFileNameWithoutExtension(p) ?? "").Where(n => n.Length > 0).ToList();
        var reruns = new List<(ProjectNode Node, string[] Names)>();
        foreach (var (path, tests) in byProject.OrderBy(p => p.Key.Absolute, StringComparer.Ordinal))
        {
            var node = graph.Find(path);
            if (node is null || !node.IsTest)
            {
                leftOut.Add(node?.Name ?? Path.GetFileNameWithoutExtension(path.Absolute));
                continue;
            }

            var names = tests.Select(StripTheoryArgs).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            if (names.Length > 0)
                reruns.Add((node, names));
        }

        var runner = GlobalJson.UsesTestingPlatform(_workspace.Root.Path, _workspace.Root.Path);
        var projects = new List<PlannedProjectWork>();
        var collapsed = new List<string>();
        var platformCollapsed = new List<string>();
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var selected = 0;
        var snapshot = generation.Current;
        var selections = new Dictionary<RepoPath, TestSelection>();
        foreach (var (node, names) in reruns)
        {
            TestSelection selection = new TestSelection.Methods([.. names]);
            selections[node.Path] = selection;
            var tests = _counter.TestsIn(node);
            // A platform project whose framework Fuse cannot filter runs whole, so every test in it counts as run.
            selected += node.UsesTestingPlatform && node.Framework == TestFramework.Unknown
                ? tests.Count
                : TestCounter.Count(tests, selection);
            if (node.UsesTestingPlatform)
            {
                var (platformFilter, arguments) = PlatformRun(node, selection, runner, collapsed, platformCollapsed, reasons);
                projects.Add(new PlannedProjectWork(node, platformFilter, arguments, RepoWorkspace.ProjectsFor(snapshot, node).ToList()));
                continue;
            }

            var filter = TestFilter.For(selection);
            if (filter.Collapse != FilterCollapse.None)
            {
                var form = filter.Collapse == FilterCollapse.ToClasses ? $"{filter.Patterns} class prefix(es)" : "no filter, so the project runs whole";
                _workspace.Log($"test plan: {node.Name} filter of {names.Length} pattern(s) is above {TestFilter.MaxPatterns}, collapsed to {form}");
                NoteCollapse(node, filter.Collapse, collapsed);
            }

            projects.Add(new PlannedProjectWork(node, filter.Expression, [], RepoWorkspace.ProjectsFor(snapshot, node).ToList()));
        }

        phases.Add(Phase.Selection, timer);
        var summary = new StringBuilder($"reran {reruns.Sum(r => r.Names.Length)} test(s) that failed last time in {reruns.Count} test project(s)");
        if (reasons.Count > 0)
            summary.Append(" (whole projects where ").Append(string.Join("; ", reasons)).Append(')');
        if (collapsed.Count > 0)
            summary.Append("; ran ").Append(string.Join(", ", collapsed)).Append(", because a test filter holds at most ").Append(TestFilter.MaxPatterns).Append(" names");
        if (platformCollapsed.Count > 0)
            summary.Append("; ran ").Append(string.Join(", ", platformCollapsed)).Append(", because a platform filter holds at most ").Append(PlatformFilter.MaxNames).Append(" names");
        foreach (var name in leftOut.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
            summary.Append("; left out ").Append(name).Append(", which is not a test project in this repository");
        if (reruns.Count == 0)
            return PendingTestPlan.Of(new TestPlanResult([], 0, total, summary.ToString()), generation);
        return PendingTestPlan.ToPrepare(generation, EmitterFor(generation), projects, selected, total, summary.ToString(), noCache: noCache, runner: runner, selections: selections);
    }

}
