namespace Fuse.Languages.Abstractions;

/// <summary>
///     Base contract for any per-language plugin resolved by file extension.
/// </summary>
public interface ILanguageCapability
{
    /// <summary>File extensions this capability handles, including the leading dot.</summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }
}
