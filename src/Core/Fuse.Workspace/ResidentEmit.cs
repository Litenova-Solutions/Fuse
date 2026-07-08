using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;

namespace Fuse.Workspace;

/// <summary>
///     The emitted output of a resident project (T1): the on-disk path of the compiled assembly plus the file
///     paths of every metadata reference it needs to load and run. This is the input the out-of-process
///     test-runner micro-host materializes into a scratch directory and executes the covering tests against,
///     without an MSBuild build.
/// </summary>
/// <param name="AssemblyPath">The path of the emitted assembly in the scratch directory.</param>
/// <param name="ReferencePaths">The file paths of the metadata references the assembly depends on to load.</param>
public sealed record ResidentEmitOutput(string AssemblyPath, ImmutableArray<string> ReferencePaths);

/// <summary>
///     Emits a resident project's speculative compilation to a scratch directory so the out-of-process test runner
///     (T1) can load and execute it. The compilation is already rehydrated and build-exact, so emit reproduces the
///     assembly the build would have produced, including any staged edit applied to the held state, with no
///     MSBuild build.
/// </summary>
/// <remarks>
///     Emit failure (a compilation with errors) returns null rather than an unusable assembly: the caller then
///     reports the diagnostics or degrades to a build-grade run, never a false "ran" on an assembly that did not
///     compile. Only file-backed metadata references are returned; an in-memory reference (rare for a rehydrated
///     build) has no path to materialize and is skipped.
/// </remarks>
public static class ResidentEmit
{
    /// <summary>
    ///     Emits the project's compilation to <paramref name="scratchDirectory" /> and returns the emitted assembly
    ///     path plus its reference paths, or null when the compilation does not emit cleanly.
    /// </summary>
    /// <param name="project">The resident project whose compilation to emit.</param>
    /// <param name="scratchDirectory">The directory to write the emitted assembly into (created if absent).</param>
    /// <param name="cancellationToken">A token to cancel the emit.</param>
    /// <returns>The emit output, or null when emit failed (the compilation had errors).</returns>
    public static ResidentEmitOutput? EmitToDirectory(
        ResidentProject project, string scratchDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(scratchDirectory);
        var assemblyName = string.IsNullOrWhiteSpace(project.Compilation.AssemblyName)
            ? "resident"
            : project.Compilation.AssemblyName!;
        var assemblyPath = Path.Combine(scratchDirectory, assemblyName + ".dll");

        EmitResult result;
        using (var stream = File.Create(assemblyPath))
        {
            result = project.Compilation.Emit(stream, cancellationToken: cancellationToken);
        }

        if (!result.Success)
        {
            try { File.Delete(assemblyPath); } catch (IOException) { /* best effort: leave nothing half-emitted */ }
            return null;
        }

        var referencePaths = project.Compilation.References
            .OfType<PortableExecutableReference>()
            .Select(r => r.FilePath)
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!)
            .ToImmutableArray();

        return new ResidentEmitOutput(assemblyPath, referencePaths);
    }
}
