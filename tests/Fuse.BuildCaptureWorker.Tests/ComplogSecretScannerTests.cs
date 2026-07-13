using Fuse.BuildCaptureWorker;
using Fuse.Reduction.Security;
using Xunit;

namespace Fuse.BuildCaptureWorker.Tests;

// C2: the fail-closed secret scan for a capture bundle. The unit under test is the pure scan core (FindFirstSecret
// over labelled texts), so a planted secret is detected deterministically without a build. A finding names the
// match class and the artifact, never the secret value; the caller fails the capture closed on any finding.
public sealed class ComplogSecretScannerTests
{
    private static readonly ISecretRedactor Redactor = new DefaultSecretRedactor();

    [Fact]
    public void Clean_texts_produce_no_finding()
    {
        var texts = new[]
        {
            ("generated:Widget.g.cs", "namespace Sample; public sealed class Widget { public int Spin() => 42; }"),
            ("additionalfile:appsettings.json", "{ \"Logging\": { \"LogLevel\": { \"Default\": \"Information\" } } }"),
        };

        Assert.Null(ComplogSecretScanner.FindFirstSecret(texts, Redactor));
    }

    [Fact]
    public void A_planted_secret_fails_closed_naming_the_class_and_artifact_not_the_value()
    {
        // A generated document that embedded an AWS access key (the canonical AWS docs example key) at build time.
        const string secret = "AKIAIOSFODNN7EXAMPLE";
        var texts = new[]
        {
            ("generated:Config.g.cs", $"namespace Sample; static class Config {{ public const string Key = \"{secret}\"; }}"),
        };

        var finding = ComplogSecretScanner.FindFirstSecret(texts, Redactor);

        Assert.NotNull(finding);
        Assert.Equal("aws-access-key", finding!.Kind);
        Assert.Equal("generated:Config.g.cs", finding.Label);
        // The report names the class and artifact; it never carries the secret value.
        Assert.DoesNotContain(secret, finding.Kind);
        Assert.DoesNotContain(secret, finding.Label);
    }

    [Fact]
    public void The_first_secret_across_artifacts_is_reported()
    {
        var texts = new[]
        {
            ("generated:Clean.g.cs", "namespace Sample; class Clean { }"),
            ("additionalfile:secrets.config", "aws=AKIAIOSFODNN7EXAMPLE"),
        };

        var finding = ComplogSecretScanner.FindFirstSecret(texts, Redactor);

        Assert.NotNull(finding);
        Assert.Equal("additionalfile:secrets.config", finding!.Label);
    }

    [Fact]
    public void Regex_generator_output_is_not_scanned_for_high_entropy_false_positives()
    {
        // RegexGenerator.g.cs carries long mixed-case pattern literals that look like API keys.
        const string patternLiteral = "aB3+/xYz9mN2pQ7rS1tU4vW6yZ8aC0dE2fG4hJ6kL8nP0qR2sT4uV6wX8yZ0";
        var texts = new[]
        {
            ("generated:RegexGenerator.g.cs", $"namespace Fuse.Generated; static partial class Regex {{ private const string P = \"{patternLiteral}\"; }}"),
            ("generated:Config.g.cs", $"namespace Sample; static class Config {{ public const string Key = \"{patternLiteral}\"; }}"),
        };

        var finding = ComplogSecretScanner.FindFirstSecret(texts, Redactor);

        Assert.NotNull(finding);
        Assert.Equal("generated:Config.g.cs", finding!.Label);
    }
}
