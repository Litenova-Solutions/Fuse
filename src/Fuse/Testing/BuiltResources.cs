using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;

namespace Fuse.Testing;

/// <summary>
///     Reads the embedded manifest resources of a built assembly, so an assembly emitted from a Roslyn compilation can
///     carry the same resources. MSBuild adds resources to the compiler's command line; the compilation Roslyn loads
///     does not include them, and code that reads its own resources (time-zone data, templates, localized strings)
///     would otherwise fail.
/// </summary>
internal static class BuiltResources
{
    /// <summary>The embedded resources of <paramref name="assemblyPath"/>; linked resources in other files are left to the copied output.</summary>
    public static List<ResourceDescription> ReadFrom(string assemblyPath)
    {
        var result = new List<ResourceDescription>();
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader is null)
            return result;
        var metadata = pe.GetMetadataReader();
        var directory = pe.PEHeaders.CorHeader.ResourcesDirectory;
        if (directory.Size == 0)
            return result;
        var block = pe.GetSectionData(directory.RelativeVirtualAddress);
        foreach (var handle in metadata.ManifestResources)
        {
            var resource = metadata.GetManifestResource(handle);
            if (!resource.Implementation.IsNil)
                continue;
            var offset = (int)resource.Offset;
            var length = block.GetReader(offset, sizeof(int)).ReadInt32();
            var data = block.GetReader(offset + sizeof(int), length).ReadBytes(length);
            var isPublic = (resource.Attributes & ManifestResourceAttributes.VisibilityMask) == ManifestResourceAttributes.Public;
            result.Add(new ResourceDescription(metadata.GetString(resource.Name), () => new MemoryStream(data, writable: false), isPublic));
        }

        return result;
    }
}
