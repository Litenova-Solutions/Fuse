using Fuse.Benchmarks;
using Xunit;

namespace Fuse.Benchmarks.Tests;

public sealed class SignalBucketTests
{
    [Theory]
    [InlineData("", SignalBucket.NoSignal)]
    [InlineData("Merge branch 'master' into master", SignalBucket.NoSignal)]
    [InlineData("Merge pull request #123 from foo/bar", SignalBucket.NoSignal)]
    [InlineData("Apply suggestions from code review", SignalBucket.NoSignal)]
    [InlineData("Bump Newtonsoft.Json from 13.0.1 to 13.0.3", SignalBucket.DependencyBump)]
    [InlineData("ci: update workflow", SignalBucket.ConfigCi)]
    [InlineData("Fix typo in comment", SignalBucket.Formatting)]
    [InlineData("Add route for billing endpoint", SignalBucket.RouteApi)]
    [InlineData("De-duping notification handlers before dispatching", SignalBucket.NaturalLanguage)]
    [InlineData("Treat blank LicenseAccessor key as unconfigured", SignalBucket.IdentifierRich)]
    public void Classify_assigns_expected_bucket(string title, string expected)
        => Assert.Equal(expected, SignalBucket.Classify(title));

    [Fact]
    public void IsLowSignal_is_true_only_for_no_signal()
    {
        Assert.True(SignalBucket.IsLowSignal(SignalBucket.NoSignal));
        Assert.False(SignalBucket.IsLowSignal(SignalBucket.IdentifierRich));
        Assert.False(SignalBucket.IsLowSignal(SignalBucket.NaturalLanguage));
    }
}
