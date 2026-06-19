using Fuse.Collection.FileSystem;

namespace Fuse.Fusion;

/// <summary>
///     Validates fusion requests before execution.
/// </summary>
public sealed class FusionValidator
{
    private readonly IFileSystem _fileSystem;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FusionValidator" /> class.
    /// </summary>
    /// <param name="fileSystem">The file system used to verify the source directory exists.</param>
    public FusionValidator(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <summary>
    ///     Validates the specified fusion request and returns all detected errors.
    /// </summary>
    /// <param name="request">The fusion request to validate.</param>
    /// <returns>A read-only list of validation error messages. Empty when the request is valid.</returns>
    public IReadOnlyList<string> Validate(FusionRequest request)
    {
        var errors = new List<string>();
        var collection = request.Collection;

        if (string.IsNullOrWhiteSpace(collection.SourceDirectory))
        {
            errors.Add("Source directory is required.");
        }
        else if (!_fileSystem.DirectoryExists(collection.SourceDirectory))
        {
            errors.Add($"Source directory does not exist: {collection.SourceDirectory}");
        }

        return errors;
    }

    /// <summary>
    ///     Validates the specified fusion request and throws when errors are detected.
    /// </summary>
    /// <param name="request">The fusion request to validate.</param>
    /// <exception cref="FusionValidationException">Thrown when validation errors are detected.</exception>
    public void ValidateOrThrow(FusionRequest request)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            throw new FusionValidationException(errors);
        }
    }

    /// <summary>
    ///     Validates builder state for contradictory template and only-extensions settings.
    /// </summary>
    /// <param name="hasTemplate">Whether a project template was specified.</param>
    /// <param name="hasOnlyExtensions">Whether only-extensions were specified.</param>
    /// <returns>A read-only list of validation error messages. Empty when the settings are compatible.</returns>
    public static IReadOnlyList<string> ValidateBuilderState(bool hasTemplate, bool hasOnlyExtensions)
    {
        if (hasTemplate && hasOnlyExtensions)
        {
            return ["OnlyExtensions cannot be used together with a project template. OnlyExtensions overrides template defaults entirely."];
        }

        return Array.Empty<string>();
    }
}
