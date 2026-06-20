using Fuse.Emission.Models;
using Fuse.Plugins.Abstractions.Patterns;
using Fuse.Plugins.Languages.CSharp.Patterns;

namespace Fuse.Plugins.Languages.CSharp.Tests.Patterns;

public class CSharpPatternDetectorTests
{
    private static FusedFileSnapshot CreateSnapshot(string content, string path = "test.cs") =>
        new(path, content);

    [Fact]
    public void DiRegistration_DetectsAddSingleton()
    {
        var detector = new DiRegistrationPatternDetector();
        var result = detector.Detect([CreateSnapshot("services.AddSingleton<IFoo, Foo>();")]);
        Assert.NotNull(result);
        Assert.True(result!.OccurrenceCount >= 1);
    }

    [Fact]
    public void DiRegistration_NoRegistrations_ReturnsNull()
    {
        var detector = new DiRegistrationPatternDetector();
        Assert.Null(detector.Detect([CreateSnapshot("var x = 1;")]));
    }

    [Fact]
    public void ExceptionHandling_DetectsCustomExceptions()
    {
        var detector = new ExceptionHandlingPatternDetector();
        var result = detector.Detect([CreateSnapshot("public class FooException : Exception { }")]);
        Assert.NotNull(result);
        Assert.Contains("FooException", result!.Summary);
    }

    [Fact]
    public void Logging_DetectsILoggerInjection()
    {
        var detector = new LoggingPatternDetector();
        var result = detector.Detect([CreateSnapshot("public Foo(ILogger<Foo> logger) { }")]);
        Assert.NotNull(result);
        Assert.True(result!.OccurrenceCount >= 1);
    }

    [Fact]
    public void Async_DetectsAsyncTask()
    {
        var detector = new AsyncPatternDetector();
        var result = detector.Detect([CreateSnapshot("public async Task RunAsync() { }")]);
        Assert.NotNull(result);
    }

    [Fact]
    public void Cqrs_DetectsIRequest()
    {
        var detector = new CqrsPatternDetector();
        var result = detector.Detect([CreateSnapshot("public class Cmd : IRequest<int> { }")]);
        Assert.NotNull(result);
    }

    [Fact]
    public void Repository_DetectsIRepository()
    {
        var detector = new RepositoryPatternDetector();
        var result = detector.Detect([CreateSnapshot("private IRepository<Order> _repo;")]);
        Assert.NotNull(result);
    }

    [Fact]
    public void PatternSummary_ToComment_FormatsAllPatterns()
    {
        var summary = new PatternSummary([
            new DetectedPattern("DI Registration", "AddSingleton (1)", 1, ["a.cs"]),
            new DetectedPattern("Logging", "ILogger detected", 1, ["b.cs"]),
        ]);

        var comment = summary.ToComment();
        Assert.Contains("DI Registration", comment);
        Assert.Contains("Logging", comment);
        Assert.Contains("fuse:patterns", comment);
    }
}
