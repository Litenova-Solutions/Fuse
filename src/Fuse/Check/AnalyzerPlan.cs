using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fuse.Check;

/// <summary>
///     How a project's analyzers run in a check. Roslyn analyzes one file on a fresh copy of the compilation and runs every
///     analyzer's compilation start actions again for each file, so an analyzer that reads the whole compilation there
///     costs a whole-project pass per file. Such an analyzer is compilation-wide: it runs once per compilation over every
///     file, and the others run file by file.
/// </summary>
/// <param name="PerFile">The analyzers that run on each file a check binds.</param>
/// <param name="CompilationWide">The analyzers that run once per compilation, with the time each took per file before it was moved.</param>
internal sealed record AnalyzerPlan(
    ImmutableArray<DiagnosticAnalyzer> PerFile,
    ImmutableArray<(DiagnosticAnalyzer Analyzer, TimeSpan PerFile)> CompilationWide)
{
    /// <summary>
    ///     An analyzer whose start costs this much per file runs once per compilation instead. A run over the whole
    ///     compilation binds every file of the project, so it pays off only for an analyzer that costs about as much on
    ///     each file: most analyzers start in well under 100 ms, and one that reads the whole compilation takes seconds on
    ///     a large project.
    /// </summary>
    public static readonly TimeSpan CompilationWideStart = TimeSpan.FromSeconds(1);

    /// <summary>The compilation-wide analyzers alone.</summary>
    public ImmutableArray<DiagnosticAnalyzer> CompilationWideAnalyzers { get; } = [.. CompilationWide.Select(c => c.Analyzer)];
}
