using Fuse.Languages.Abstractions.Options;

namespace Fuse.Reduction;

/// <summary>
///     Determines whether format reduction is enabled for an extension given current options.
/// </summary>
internal static class ReductionGate
{
    internal static bool ShouldReduce(string extension, ReductionOptions options, bool hasReducer)
    {
        if (!hasReducer)
            return false;

        if (string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".razor", StringComparison.OrdinalIgnoreCase))
            return true;

        if (extension is ".cshtml" or ".html" or ".htm")
            return options.MinifyHtmlAndRazor;

        if (extension is ".css" or ".scss" or ".js" or ".json" or ".md" or ".yaml" or ".yml")
            return true;

        if (extension is ".xml" or ".targets" or ".props" or ".csproj")
            return options.MinifyXmlFiles;

        return true;
    }
}
