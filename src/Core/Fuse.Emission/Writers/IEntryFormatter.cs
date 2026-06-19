using Fuse.Emission.Models;
using Fuse.Reduction.Models;

namespace Fuse.Emission.Writers;

/// <summary>
///     Renders a single fused file entry. Shared by all output writers and formatters.
/// </summary>
public interface IEntryFormatter
{
    /// <summary>
    ///     Formats one entry, including any open and close markers for the target format.
    /// </summary>
    /// <param name="content">The fused content entry to render.</param>
    /// <param name="options">
    ///     The emission options controlling optional output, such as
    ///     <see cref="EmissionOptions.IncludeMetadata" /> and <see cref="EmissionOptions.IncludeProvenance" />.
    /// </param>
    /// <returns>The rendered entry text, terminated by a trailing newline.</returns>
    string FormatEntry(FusedContent content, EmissionOptions options);
}
