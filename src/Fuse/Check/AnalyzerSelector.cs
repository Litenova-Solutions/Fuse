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
    private readonly ConditionalWeakTable<IReadOnlyList<AnalyzerReference>, DiagnosticAnalyzer[]> _analyzers = new();
    private readonly ConditionalWeakTable<Compilation, Holder> _withAnalyzers = new();

    /// <summary>Returns the compilation wrapped with its error-capable analyzers, or null when none can report an error.</summary>
    public CompilationWithAnalyzers? For(Project project, Compilation compilation) =>
        _withAnalyzers.GetValue(compilation, c => new Holder(Create(project, c))).Value;

    private CompilationWithAnalyzers? Create(Project project, Compilation compilation)
    {
        var all = _analyzers.GetValue(project.AnalyzerReferences, refs => [.. refs.SelectMany(r => r.GetAnalyzers(LanguageNames.CSharp))]);
        if (all.Length == 0)
            return null;
        var config = ErrorConfig.Read(project, compilation);
        var selected = all.Where(a => a.SupportedDiagnostics.Any(d => config.CanBeError(d, compilation.Options))).ToImmutableArray();
        return selected.IsEmpty
            ? null
            : compilation.WithAnalyzers(selected, new CompilationWithAnalyzersOptions(project.AnalyzerOptions, onAnalyzerException: null, concurrentAnalysis: true, logAnalyzerExecutionTime: false));
    }

    private sealed class Holder(CompilationWithAnalyzers? value)
    {
        public CompilationWithAnalyzers? Value { get; } = value;
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
