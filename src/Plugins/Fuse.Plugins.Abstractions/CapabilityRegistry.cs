namespace Fuse.Plugins.Abstractions;

/// <summary>
///     Resolves a single <typeparamref name="TCapability"/> implementation by file extension.
///     Last registration for a given extension wins, allowing a specialized module to override a default.
/// </summary>
/// <typeparam name="TCapability">The language capability type to resolve, such as <see cref="Reducers.IContentReducer" />.</typeparam>
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

    /// <summary>
    ///     Resolves the registered capability for a file extension.
    /// </summary>
    /// <param name="extension">The file extension to resolve, including the leading dot. Matched case-insensitively.</param>
    /// <returns>
    ///     The capability registered for <paramref name="extension" />, or <see langword="null" /> when none is
    ///     registered.
    /// </returns>
    public TCapability? TryResolve(string extension) =>
        _byExtension.TryGetValue(extension, out var capability) ? capability : null;
}
