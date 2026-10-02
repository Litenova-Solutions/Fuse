using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;

namespace Fuse.Testing;

/// <summary>
///     The assemblies emitted for shadow runs, kept across plans: one image per build output path, so one per target
///     framework of each project. An image is reused while its project's dependent version and the write time of its
///     build output stay the same, so a project that has not changed since the last plan is not emitted again.
/// </summary>
/// <remarks>
///     The dependent version changes when a document, a project attribute or a reference of the project or of any
///     project it depends on changes. The build output's write time is in the key because the image carries the embedded
///     resources of the last built assembly. Only the bytes are kept, never a compilation, so the cache holds at most one
///     image (assembly and PDB) per build output path. Not thread-safe: <see cref="TestPlanner"/> prepares one plan at a time.
/// </remarks>
internal sealed class EmitCache
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times an assembly was emitted, as opposed to taken from the cache.</summary>
    public int Emits { get; private set; }

    /// <summary>
    ///     The image of <paramref name="project"/> as its compilation in this snapshot has it, emitted once per change, or
    ///     null when the project does not compile. A reused image is the same <see cref="EmittedImage"/> instance.
    /// </summary>
    /// <param name="project">A project with build output, from the snapshot the plan took.</param>
    /// <param name="cancellationToken">Cancels emitting.</param>
    public async Task<EmittedImage?> GetAsync(Project project, CancellationToken cancellationToken)
    {
        var output = project.OutputFilePath!;
        var version = await project.GetDependentVersionAsync(cancellationToken).ConfigureAwait(false);
        var built = File.GetLastWriteTimeUtc(output);
        if (_entries.TryGetValue(output, out var cached) && cached.Project == project.Id && cached.Version == version && cached.BuiltUtc == built)
            return cached.Image;

        var image = await EmitAsync(project, output, cancellationToken).ConfigureAwait(false);
        _entries[output] = new Entry(project.Id, version, built, image);
        return image;
    }

    private async Task<EmittedImage?> EmitAsync(Project project, string output, CancellationToken cancellationToken)
    {
        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null)
            return null;
        Emits++;
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var pdbPath = Path.ChangeExtension(Path.GetFileName(output), ".pdb");
        var result = compilation.Emit(
            pe,
            pdb,
            manifestResources: BuiltResources.ReadFrom(output),
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: pdbPath),
            cancellationToken: cancellationToken);
        return result.Success ? new EmittedImage(pe.ToArray(), pdb.ToArray()) : null;
    }

    /// <summary>What an image was emitted from, and the image, or null when the project did not compile.</summary>
    private sealed record Entry(ProjectId Project, VersionStamp Version, DateTime BuiltUtc, EmittedImage? Image);
}
