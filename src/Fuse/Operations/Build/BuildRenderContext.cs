using Fuse.Dotnet.Processes;
namespace Fuse.Operations.Build;

/// <summary>What a finished build render prints: the process, where it ran, how long it took, and how to show it.</summary>
/// <param name="Result">The finished <c>dotnet build</c> process.</param>
/// <param name="Root">The repository the errors print relative to.</param>
/// <param name="Seconds">How long the build took.</param>
/// <param name="Operation">What ran, as the summary names it.</param>
/// <param name="Options">How many errors to print, in what form, and whether the warnings are listed.</param>
/// <param name="ResultsPath">Where every error and warning was written, or null when it was not written.</param>
/// <param name="Log">The build's file log, or empty when it has none.</param>
/// <param name="TreeStamp">The stamp a <c>fuse build</c> summary carries, or null to print none.</param>
/// <param name="FileTotal">How many errors the results file holds when it holds more than this build's, or null.</param>
/// <param name="RestoreTarget">What a lock refresh restores, relative to the root, or null to derive it from the repository.</param>
/// <param name="Passthrough">The arguments for <c>dotnet build</c>, unchanged, or null when the <c>--all-errors</c> hint names none.</param>
/// <param name="Changed">True when the build covers the changed projects, so the <c>--all-errors</c> hint keeps <c>--changed</c>.</param>
internal sealed record BuildRenderContext(
    ProcessResult Result,
    string Root,
    double Seconds,
    string Operation,
    BuildOptions Options,
    string? ResultsPath = null,
    string Log = "",
    string? TreeStamp = null,
    int? FileTotal = null,
    string? RestoreTarget = null,
    IReadOnlyList<string>? Passthrough = null,
    bool Changed = false);
