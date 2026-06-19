namespace Fuse.Plugins.Abstractions;

/// <summary>
///     Base contract for any per-language plugin resolved by file extension through
///     <see cref="CapabilityRegistry{TCapability}" />.
/// </summary>
public interface ILanguageCapability
{
    /// <summary>
    ///     The file extensions this capability handles, each including the leading dot (for example, <c>.cs</c>).
    ///     Matched case-insensitively during resolution.
    /// </summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }
}
