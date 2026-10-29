using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fuse.Check;

/// <summary>
///     Picks the analyzers that can report an error under a project's configuration, so a check runs the few that
///     could fail the build instead of every analyzer the project references.
/// </summary>
/// <remarks>
///     A descriptor can end up an error through its default severity, a project-wide option (WarningsAsErrors,
///     TreatWarningsAsErrors), an editorconfig or globalconfig <c>dotnet_diagnostic.ID.severity</c>, or a bulk
///     <c>dotnet_analyzer_diagnostic</c> severity. Any of those qualifies the analyzer; reported diagnostics are
///     filtered to errors afterwards, so over-selecting only costs time.
/// </remarks>
internal sealed class AnalyzerSelector
{
    private readonly Func<int> _configurationGeneration;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<ProjectId, (int Generation, ImmutableArray<DiagnosticAnalyzer> Analyzers)> _selected = new();
    private readonly ConditionalWeakTable<Compilation, Lazy<CompilationWithAnalyzers?>> _withAnalyzers = new();
    /// <summary>Each project's plan, shared, so the files of a check wait for their project's plan instead of each making one.</summary>
    private readonly Dictionary<ProjectId, (int Generation, Task<AnalyzerPlan> Plan)> _plans = [];

    private readonly Lock _planning = new();

    /// <param name="configurationGeneration">Changes whenever project configuration (project files, editorconfig) may have changed.</param>
    /// <param name="log">Where each plan is written, naming the compilation-wide analyzers.</param>
    public AnalyzerSelector(Func<int> configurationGeneration, Action<string> log)
    {
        _configurationGeneration = configurationGeneration;
        _log = log;
    }

    /// <summary>
    ///     The project's <see cref="AnalyzerPlan"/>, made once per project until the configuration changes. Making it runs
    ///     every analyzer on the file of <paramref name="probe"/>, syntax then semantics, and reads the time each took: an analyzer that
    ///     registers compilation start or compilation actions and took at least <see cref="AnalyzerPlan.CompilationWideStart"/>
    ///     on one file, timed on a second run, is compilation-wide. The probe runs on a wrapper of its own that times the analyzers; the wrapper
    ///     every check uses does not, because timing every analyzer callback costs time on every file.
    /// </summary>
    public Task<AnalyzerPlan> PlanAsync(Project project, CompilationWithAnalyzers analyzers, SemanticModel probe, CancellationToken cancellationToken)
    {
        var generation = _configurationGeneration();
        lock (_planning)
        {
            if (_plans.TryGetValue(project.Id, out var known) && known.Generation == generation && !known.Plan.IsCanceled && !known.Plan.IsFaulted)
                return known.Plan;
            var plan = Task.Run(() => MakePlanAsync(project, analyzers, probe, cancellationToken), cancellationToken);
            _plans[project.Id] = (generation, plan);
            return plan;
        }
    }

    private async Task<AnalyzerPlan> MakePlanAsync(Project project, CompilationWithAnalyzers analyzers, SemanticModel probe, CancellationToken cancellationToken)
    {
        // The first run also pays for compiling the analyzers' code and Roslyn's own, which can make an analyzer that
        // starts in milliseconds take over a second once, so only a second run is timed.
        await ProbeAsync(project, analyzers, probe, cancellationToken).ConfigureAwait(false);
        var result = await ProbeAsync(project, analyzers, probe, cancellationToken).ConfigureAwait(false);
        var perFile = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        var compilationWide = ImmutableArray.CreateBuilder<(DiagnosticAnalyzer, TimeSpan)>();
        foreach (var analyzer in analyzers.Analyzers)
        {
            // Only compilation start and compilation actions run again for each file; an analyzer without them that took
            // long did so on this file's own content, which no other way of running it would save.
            if (result.AnalyzerTelemetryInfo.TryGetValue(analyzer, out var info)
                && (info.CompilationStartActionsCount > 0 || info.CompilationActionsCount > 0)
                && info.ExecutionTime >= AnalyzerPlan.CompilationWideStart)
                compilationWide.Add((analyzer, info.ExecutionTime));
            else
                perFile.Add(analyzer);
        }

        var declarationOnly = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        foreach (var (analyzer, _) in compilationWide)
        {
            if (await DeclarationOnlyProbe.ClassifyAsync(probe.Compilation, analyzer, project.AnalyzerOptions, cancellationToken).ConfigureAwait(false))
                declarationOnly.Add(analyzer);
        }

        var plan = new AnalyzerPlan(perFile.ToImmutable(), compilationWide.ToImmutable()) { DeclarationOnly = declarationOnly.ToImmutable() };
        if (!plan.CompilationWide.IsEmpty)
            _log($"analyzers of {project.Name}: {plan.PerFile.Length} per file, {plan.CompilationWide.Length} compilation-wide ({string.Join(", ", plan.CompilationWide.Select(c => $"{Name(c.Analyzer)} {c.PerFile.TotalMilliseconds:0} ms on one file"))}{(plan.DeclarationOnly.IsEmpty ? "" : $"; declaration-only: {string.Join(", ", plan.DeclarationOnly.Select(Name))}")})");
        return plan;
    }

    /// <summary>Runs every analyzer on the file of <paramref name="probe"/>, syntax then semantics, on a wrapper of its own that times them.</summary>
    private static async Task<AnalysisResult> ProbeAsync(Project project, CompilationWithAnalyzers analyzers, SemanticModel probe, CancellationToken cancellationToken)
    {
        var timed = probe.Compilation.WithAnalyzers(analyzers.Analyzers, Options(project, logExecutionTime: true));
        await timed.GetAnalysisResultAsync(probe.SyntaxTree, timed.Analyzers, cancellationToken).ConfigureAwait(false);
        return await timed.GetAnalysisResultAsync(probe, filterSpan: null, timed.Analyzers, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>An analyzer's assembly and type name, as a log line names it.</summary>
    public static string Name(DiagnosticAnalyzer analyzer) =>
        $"{analyzer.GetType().Assembly.GetName().Name}/{analyzer.GetType().Name}";

    /// <summary>Returns the compilation wrapped with its error-capable analyzers, or null when none can report an error.</summary>
    /// <remarks>
    ///     The selection depends only on the project's analyzers and configuration, so it is computed once per project
    ///     until the configuration changes. The wrapper is per compilation, created once even when files bind in parallel.
    /// </remarks>
    public CompilationWithAnalyzers? For(Project project, Compilation compilation) =>
        _withAnalyzers.GetValue(
            compilation,
            c => new Lazy<CompilationWithAnalyzers?>(() => Create(project, c), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private CompilationWithAnalyzers? Create(Project project, Compilation compilation)
    {
        var generation = _configurationGeneration();
        if (!_selected.TryGetValue(project.Id, out var selected) || selected.Generation != generation)
        {
            selected = (generation, Select(project, compilation));
            _selected[project.Id] = selected;
        }

        return selected.Analyzers.IsEmpty
            ? null
            : compilation.WithAnalyzers(selected.Analyzers, Options(project, logExecutionTime: false));
    }

    private static CompilationWithAnalyzersOptions Options(Project project, bool logExecutionTime) =>
        new(project.AnalyzerOptions, onAnalyzerException: null, concurrentAnalysis: true, logAnalyzerExecutionTime: logExecutionTime);

    private static ImmutableArray<DiagnosticAnalyzer> Select(Project project, Compilation compilation)
    {
        var all = project.AnalyzerReferences.SelectMany(r => r.GetAnalyzers(LanguageNames.CSharp)).ToList();
        if (all.Count == 0)
            return [];
        var config = ErrorConfig.Read(project, compilation);
        return [.. all.Where(a => a.SupportedDiagnostics.Any(d => config.CanBeError(d, compilation.Options)))];
    }

    /// <summary>
    ///     How a compilation's configuration sets severities. Editorconfig and globalconfig
    ///     <c>dotnet_diagnostic.ID.severity</c> entries are not visible through <see cref="AnalyzerConfigOptions"/>; the
    ///     compiler exposes them through <see cref="SyntaxTreeOptionsProvider"/>, per tree and globally. Trees that share
    ///     an options set share their severities, so one tree per distinct set is enough to consult.
    /// </summary>
    private sealed class ErrorConfig
    {
        private readonly HashSet<string> _categories = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<SyntaxTree> _representatives = [];
        private SyntaxTreeOptionsProvider? _provider;
        private bool _bulk;

        public static ErrorConfig Read(Project project, Compilation compilation)
        {
            var result = new ErrorConfig { _provider = compilation.Options.SyntaxTreeOptionsProvider };
            var provider = project.AnalyzerOptions.AnalyzerConfigOptionsProvider;
            var seen = new HashSet<AnalyzerConfigOptions>(ReferenceEqualityComparer.Instance);
            foreach (var tree in compilation.SyntaxTrees)
            {
                var options = provider.GetOptions(tree);
                if (seen.Add(options))
                {
                    result._representatives.Add(tree);
                    result.ReadBulk(options);
                }
            }

            result.ReadBulk(provider.GlobalOptions);
            return result;
        }

        private void ReadBulk(AnalyzerConfigOptions options)
        {
            foreach (var key in options.Keys)
            {
                if (!options.TryGetValue(key, out var value) || !IsError(value))
                    continue;
                if (key.StartsWith("dotnet_analyzer_diagnostic.category-", StringComparison.OrdinalIgnoreCase) && key.EndsWith(".severity", StringComparison.OrdinalIgnoreCase))
                    _categories.Add(key["dotnet_analyzer_diagnostic.category-".Length..^".severity".Length]);
                else if (key.Equals("dotnet_analyzer_diagnostic.severity", StringComparison.OrdinalIgnoreCase))
                    _bulk = true;
            }
        }

        private static bool IsError(string value) => value.Trim().StartsWith("error", StringComparison.OrdinalIgnoreCase);

        public bool CanBeError(DiagnosticDescriptor descriptor, CompilationOptions options)
        {
            if (_categories.Contains(descriptor.Category) || _bulk)
                return true;

            // A file-level setting wins over every project-level one, so a file that raises the id to error decides.
            // If every file sets the id to something lower, no file can report it as an error.
            var everyFileLower = _representatives.Count > 0;
            foreach (var tree in _representatives)
            {
                if (_provider is not null && _provider.TryGetDiagnosticValue(tree, descriptor.Id, CancellationToken.None, out var perTree))
                {
                    if (perTree == ReportDiagnostic.Error || (perTree == ReportDiagnostic.Warn && options.GeneralDiagnosticOption == ReportDiagnostic.Error))
                        return true;
                }
                else
                {
                    everyFileLower = false;
                }
            }

            if (everyFileLower)
                return false;
            if (_provider is not null && _provider.TryGetGlobalDiagnosticValue(descriptor.Id, CancellationToken.None, out var global))
                return global == ReportDiagnostic.Error || (global == ReportDiagnostic.Warn && options.GeneralDiagnosticOption == ReportDiagnostic.Error);
            if (options.SpecificDiagnosticOptions.TryGetValue(descriptor.Id, out var specific))
                return specific == ReportDiagnostic.Error || (specific == ReportDiagnostic.Warn && options.GeneralDiagnosticOption == ReportDiagnostic.Error);
            if (!descriptor.IsEnabledByDefault)
                return false;
            return descriptor.DefaultSeverity == DiagnosticSeverity.Error
                   || (descriptor.DefaultSeverity == DiagnosticSeverity.Warning && options.GeneralDiagnosticOption == ReportDiagnostic.Error);
        }
    }
}
