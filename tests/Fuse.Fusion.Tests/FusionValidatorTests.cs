using Fuse.Collection.FileSystem;
using Fuse.Collection.Options;
using Fuse.Fusion;

namespace Fuse.Fusion.Tests;

public sealed class FusionValidatorTests
{
    [Fact]
    public void Validate_MissingSourceDirectory_ReturnsError()
    {
        var validator = new FusionValidator(new StubFileSystem(exists: true));
        var request = CreateRequest(sourceDirectory: "   ");

        var errors = validator.Validate(request);

        Assert.Contains("Source directory is required.", errors);
    }

    [Fact]
    public void Validate_NonexistentSourceDirectory_ReturnsError()
    {
        var validator = new FusionValidator(new StubFileSystem(exists: false));
        var request = CreateRequest(sourceDirectory: @"C:\missing\path");

        var errors = validator.Validate(request);

        Assert.Contains("Source directory does not exist: C:\\missing\\path", errors);
    }

    [Fact]
    public void Validate_ValidSourceDirectory_ReturnsNoErrors()
    {
        var validator = new FusionValidator(new StubFileSystem(exists: true));
        var request = CreateRequest(sourceDirectory: @"C:\src");

        var errors = validator.Validate(request);

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateBuilderState_TemplateAndOnlyExtensions_ReturnsConflict()
    {
        var errors = FusionValidator.ValidateBuilderState(hasTemplate: true, hasOnlyExtensions: true);

        Assert.Single(errors);
        Assert.Contains("OnlyExtensions cannot be used together with a project template", errors[0]);
    }

    [Fact]
    public void ValidateBuilderState_TemplateOnly_ReturnsNoErrors()
    {
        var errors = FusionValidator.ValidateBuilderState(hasTemplate: true, hasOnlyExtensions: false);

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateBuilderState_OnlyExtensionsOnly_ReturnsNoErrors()
    {
        var errors = FusionValidator.ValidateBuilderState(hasTemplate: false, hasOnlyExtensions: true);

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateOrThrow_InvalidRequest_ThrowsFusionValidationException()
    {
        var validator = new FusionValidator(new StubFileSystem(exists: false));
        var request = CreateRequest(sourceDirectory: @"C:\missing");

        var exception = Assert.Throws<FusionValidationException>(() => validator.ValidateOrThrow(request));

        Assert.NotEmpty(exception.Errors);
    }

    private static FusionRequest CreateRequest(string sourceDirectory) =>
        new(
            new CollectionOptions(sourceDirectory),
            new Fuse.Reduction.Options.ReductionOptions(),
            new Fuse.Emission.Models.EmissionOptions());

    private sealed class StubFileSystem(bool exists) : IFileSystem
    {
        public bool DirectoryExists(string path) => exists;

        public void CreateDirectory(string path) { }

        public IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption) =>
            [];

        public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public FileInfo GetFileInfo(string path) => new(path);

        public bool IsBinaryFile(string filePath) => false;

        public string GetRelativePath(string relativeTo, string path) => path;
    }
}
