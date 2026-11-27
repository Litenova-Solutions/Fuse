using Fuse.Check.Pipeline;
using Microsoft.CodeAnalysis;
namespace Fuse.Check.Diagnostics;

/// <summary>
///     What one run of a file's analyzers needs: the project and model to run on, whether the run is over the
///     baseline, whether the per-file analyzers run on this file, the check the run counts in, and whether the
///     file is bound as part of a whole project. The token that stops the wait travels with the call, not the request.
/// </summary>
/// <param name="Project">The project the model belongs to.</param>
/// <param name="Model">The model to run the per-file analyzers on.</param>
/// <param name="Baseline">True for a run over the baseline, false for one over the working tree.</param>
/// <param name="PerFile">True to run the per-file analyzers on the file, false to run only the compilation-wide ones it shares.</param>
/// <param name="Context">The check the run counts in.</param>
/// <param name="WholeProject">True when the file is bound as part of a whole project, so an unfinished run counts the project once by its name.</param>
internal sealed record AnalyzerExecutionRequest(
    Project Project,
    SemanticModel Model,
    bool Baseline,
    bool PerFile,
    CheckContext Context,
    bool WholeProject);
