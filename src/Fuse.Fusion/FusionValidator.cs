using Fuse.Analysis.Changes;
using Fuse.Analysis.Dependencies;
using Fuse.Analysis.Search;
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
    public FusionValidator(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <summary>
    ///     Validates the specified fusion request and returns all detected errors.
    /// </summary>
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

        ValidateScopingMutualExclusivity(request, errors);
        ValidateDepthConstraints(request, errors);
        ValidateQueryConstraints(request, errors);

        return errors;
    }

    /// <summary>
    ///     Validates the specified fusion request and throws when errors are detected.
    /// </summary>
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
    public static IReadOnlyList<string> ValidateBuilderState(bool hasTemplate, bool hasOnlyExtensions)
    {
        if (hasTemplate && hasOnlyExtensions)
        {
            return ["OnlyExtensions cannot be used together with a project template. OnlyExtensions overrides template defaults entirely."];
        }

        return Array.Empty<string>();
    }

    private static void ValidateScopingMutualExclusivity(FusionRequest request, List<string> errors)
    {
        var activeModes = 0;
        if (request.Focus is not null) activeModes++;
        if (request.Changes is not null) activeModes++;
        if (request.Query is not null) activeModes++;

        if (activeModes <= 1)
            return;

        errors.Add(
            "FocusOptions, ChangeOptions, and QueryOptions are mutually exclusive. Remove all but one scoping mode.");
    }

    private static void ValidateDepthConstraints(FusionRequest request, List<string> errors)
    {
        if (request.Focus is not null && (request.Focus.Depth < 1 || request.Focus.Depth > 10))
        {
            errors.Add("FocusOptions.Depth must be between 1 and 10.");
        }

        if (request.Query is not null && (request.Query.Depth < 1 || request.Query.Depth > 10))
        {
            errors.Add("QueryOptions.Depth must be between 1 and 10.");
        }
    }

    private static void ValidateQueryConstraints(FusionRequest request, List<string> errors)
    {
        if (request.Query is null)
            return;

        if (string.IsNullOrWhiteSpace(request.Query.Query))
        {
            errors.Add("QueryOptions.Query is required when query scoping is enabled.");
        }

        if (request.Query.TopFiles < 1)
        {
            errors.Add("QueryOptions.TopFiles must be at least 1.");
        }
    }
}
