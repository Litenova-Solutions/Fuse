using Fuse.Collection.Models;
using Fuse.Collection.Options;

namespace Fuse.Collection.Filters;

/// <summary>
///     Includes files whose names match one of the configured extensions.
/// </summary>
/// <remarks>
///     When <see cref="CollectionOptions.Extensions" /> contains <c>*.*</c>, all extensions are allowed.
/// </remarks>
public sealed class ExtensionFilter : IFileFilter
{
    /// <inheritdoc />
    public bool Include(FileCandidate candidate, CollectionOptions options)
    {
        if (options.Extensions.Contains("*.*"))
            return true;

        return options.Extensions.Any(extension =>
            candidate.FileInfo.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }
}
