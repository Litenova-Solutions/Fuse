namespace Fuse.Collection.Models;

/// <summary>
///     Represents a file that passed all collection filters and is ready for reduction.
/// </summary>
/// <remarks>
///     Wraps a <see cref="FileCandidate" /> and exposes extension-aware behavioral properties
///     used by downstream reduction and emission pipelines.
/// </remarks>
public sealed class SourceFile
{
    private readonly FileCandidate _candidate;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SourceFile" /> class.
    /// </summary>
    /// <param name="candidate">The underlying file candidate that passed all filters.</param>
    public SourceFile(FileCandidate candidate)
    {
        _candidate = candidate;
    }

    /// <summary>
    ///     Gets the absolute path to the file on disk.
    /// </summary>
    public string FullPath => _candidate.FullPath;

    /// <summary>
    ///     Gets the path relative to the source directory root.
    /// </summary>
    public string RelativePath => _candidate.RelativePath;

    /// <summary>
    ///     Gets metadata for the file, including size and timestamps.
    /// </summary>
    public FileInfo FileInfo => _candidate.FileInfo;

    /// <summary>
    ///     Gets the file extension in lowercase with a leading dot, or the full file name
    ///     in lowercase when no extension is present.
    /// </summary>
    public string Extension
    {
        get
        {
            var extension = Path.GetExtension(_candidate.FileInfo.Name);
            return string.IsNullOrEmpty(extension)
                ? _candidate.FileInfo.Name.ToLowerInvariant()
                : extension.ToLowerInvariant();
        }
    }

    /// <summary>
    ///     Gets the relative path with backslashes replaced by forward slashes.
    /// </summary>
    public string NormalizedRelativePath =>
        _candidate.RelativePath.Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    ///     Gets a value indicating whether the file is a C# source file.
    /// </summary>
    public bool IsCSharp => Extension == ".cs";

    /// <summary>
    ///     Gets a value indicating whether the file is a Razor component file.
    /// </summary>
    public bool IsRazor => Extension == ".razor";

    /// <summary>
    ///     Gets a value indicating whether the file is an HTML document.
    /// </summary>
    public bool IsHtml => Extension is ".html" or ".htm";

    /// <summary>
    ///     Gets a value indicating whether the file is a CSS stylesheet.
    /// </summary>
    public bool IsCss => Extension is ".css" or ".scss" or ".less";

    /// <summary>
    ///     Gets a value indicating whether the file is a JSON document.
    /// </summary>
    public bool IsJson => Extension == ".json";

    /// <summary>
    ///     Gets a value indicating whether the file is an XML document.
    /// </summary>
    public bool IsXml => Extension is ".xml" or ".csproj" or ".config" or ".props" or ".targets";

    /// <summary>
    ///     Gets a value indicating whether the file is a Markdown document.
    /// </summary>
    public bool IsMarkdown => Extension == ".md";

    /// <summary>
    ///     Gets a value indicating whether the file is a YAML document.
    /// </summary>
    public bool IsYaml => Extension is ".yaml" or ".yml";

    /// <summary>
    ///     Gets a value indicating whether the file is a JavaScript source file.
    /// </summary>
    public bool IsJavaScript => Extension is ".js" or ".jsx" or ".mjs";
}
