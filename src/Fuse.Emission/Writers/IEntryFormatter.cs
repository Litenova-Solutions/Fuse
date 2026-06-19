using Fuse.Emission.Models;
using Fuse.Reduction.Models;

namespace Fuse.Emission.Writers;

/// <summary>
///     Renders a single fused file entry. Shared by all output writers and formatters.
/// </summary>
public interface IEntryFormatter
{
    /// <summary>Formats one entry, including any open/close markers for the target format.</summary>
    string FormatEntry(FusedContent content, EmissionOptions options);
}
