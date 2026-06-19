namespace Fuse.Languages.Abstractions;

/// <summary>
///     Resolves a single <typeparamref name="TCapability"/> implementation by file extension.
///     Last registration for a given extension wins, allowing a specialized module to override a default.
/// </summary>
public sealed class CapabilityRegistry<TCapability>
    where TCapability : class, ILanguageCapability
{
    private readonly IReadOnlyDictionary<string, TCapability> _byExtension;

    /// <summary>
    ///     Initializes a new instance of the <see cref="CapabilityRegistry{TCapability}"/> class.
    /// </summary>
    /// <param name="capabilities">Registered capability implementations.</param>
    public CapabilityRegistry(IEnumerable<TCapability> capabilities)
    {
        var map = new Dictionary<string, TCapability>(StringComparer.OrdinalIgnoreCase);
        foreach (var capability in capabilities)
        {
            foreach (var extension in capability.SupportedExtensions)
            {
                map[extension] = capability;
            }
        }

        _byExtension = map;
    }

    /// <summary>Returns the capability for the extension, or null when none is registered.</summary>
    public TCapability? TryResolve(string extension) =>
        _byExtension.TryGetValue(extension, out var capability) ? capability : null;
}
