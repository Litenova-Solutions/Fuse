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

    /// <summary>The ids and categories that any editorconfig or globalconfig section raises to error.</summary>
    private sealed class ErrorConfig
    {
        private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _categories = new(StringComparer.OrdinalIgnoreCase);
        private bool _bulk;

        public static ErrorConfig Read(Project project, Compilation compilation)
        {
            var result = new ErrorConfig();
            var provider = project.AnalyzerOptions.AnalyzerConfigOptionsProvider;
            var seen = new HashSet<AnalyzerConfigOptions>(ReferenceEqualityComparer.Instance);
            foreach (var options in compilation.SyntaxTrees.Select(provider.GetOptions).Append(provider.GlobalOptions))
            {
                if (!seen.Add(options))
                    continue;
                foreach (var key in options.Keys)
                {
                    if (!options.TryGetValue(key, out var value) || !value.Trim().StartsWith("error", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (key.StartsWith("dotnet_diagnostic.", StringComparison.OrdinalIgnoreCase) && key.EndsWith(".severity", StringComparison.OrdinalIgnoreCase))
                        result._ids.Add(key["dotnet_diagnostic.".Length..^".severity".Length]);
                    else if (key.StartsWith("dotnet_analyzer_diagnostic.category-", StringComparison.OrdinalIgnoreCase) && key.EndsWith(".severity", StringComparison.OrdinalIgnoreCase))
                        result._categories.Add(key["dotnet_analyzer_diagnostic.category-".Length..^".severity".Length]);
                    else if (key.Equals("dotnet_analyzer_diagnostic.severity", StringComparison.OrdinalIgnoreCase))
                        result._bulk = true;
                }
            }

            return result;
        }

        public bool CanBeError(DiagnosticDescriptor descriptor, CompilationOptions options)
        {
            if (_ids.Contains(descriptor.Id) || _categories.Contains(descriptor.Category) || _bulk)
                return true;
            if (options.SpecificDiagnosticOptions.TryGetValue(descriptor.Id, out var specific))
            {
                if (specific == ReportDiagnostic.Error)
                    return true;
                if (specific is ReportDiagnostic.Suppress or ReportDiagnostic.Hidden or ReportDiagnostic.Info)
                    return false;
            }

            if (!descriptor.IsEnabledByDefault)
                return false;
            return descriptor.DefaultSeverity == DiagnosticSeverity.Error
                   || (descriptor.DefaultSeverity == DiagnosticSeverity.Warning && options.GeneralDiagnosticOption == ReportDiagnostic.Error);
        }
    }
}
