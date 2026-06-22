using System.Reflection;
using Fuse.Reduction.Security;

namespace Fuse.Reduction.Tests.Security;

public sealed class SecretRedactorTests
{
    private readonly DefaultSecretRedactor _redactor = new();

    [Fact]
    public void Redact_AwsAccessKey_ReplacesInPlace()
    {
        const string input = "var key = \"AKIAIOSFODNN7EXAMPLE\";";
        var result = _redactor.Redact(input);

        Assert.Contains("[REDACTED:aws-access-key]", result.Content);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", result.Content);
        Assert.True(result.CountsByKind["aws-access-key"] >= 1);
    }

    [Fact]
    public void Redact_Jwt_ReplacesToken()
    {
        const string input =
            "token = eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";
        var result = _redactor.Redact(input);

        Assert.Contains("[REDACTED:jwt]", result.Content);
        Assert.DoesNotContain("eyJhbGciOi", result.Content);
    }

    [Fact]
    public void Redact_PemHeader_ReplacesHeader()
    {
        const string input = "-----BEGIN RSA PRIVATE KEY-----";
        var result = _redactor.Redact(input);

        Assert.Contains("[REDACTED:pem-private-key]", result.Content);
        Assert.DoesNotContain("BEGIN RSA PRIVATE KEY", result.Content);
    }

    [Fact]
    public void Redact_ConnectionStringLiteral_ReplacesCredentials()
    {
        const string input = "var cs = \"Server=db;Database=app;User ID=sa;Password=secret\";";
        var result = _redactor.Redact(input);

        Assert.Contains("[REDACTED:connection-string]", result.Content);
        Assert.DoesNotContain("Password=secret", result.Content);
        Assert.Equal(1, result.CountsByKind["connection-string"]);
    }

    [Fact]
    public void Redact_CodeAssignment_IsNotTreatedAsConnectionString()
    {
        // A single-pair assignment that the old pattern matched. It must survive untouched.
        const string input = "var x = Server = GetServer();";
        var result = _redactor.Redact(input);

        Assert.Equal(input, result.Content);
        Assert.False(result.CountsByKind.ContainsKey("connection-string"));
    }

    [Fact]
    public void Redact_TwoUnrelatedAssignmentsInLiteral_AreNotRedacted()
    {
        // Two key=value pairs but no connection keyword: not a connection string.
        const string input = "var s = \"width=100;height=200\";";
        var result = _redactor.Redact(input);

        Assert.DoesNotContain("[REDACTED:connection-string]", result.Content);
    }

    [Fact]
    public void Redact_AppSettingsJsonConnectionString_IsRedacted()
    {
        const string input = "\"Default\": \"Host=localhost;Port=5432;Database=app;Username=u;Password=p\"";
        var result = _redactor.Redact(input);

        Assert.Contains("[REDACTED:connection-string]", result.Content);
        Assert.DoesNotContain("Password=p", result.Content);
    }

    [Fact]
    public void Redact_PreservesSurroundingCode()
    {
        const string input = """
            public class Config
            {
                public string ApiKey = "AKIAIOSFODNN7EXAMPLE";
                public int Timeout = 30;
            }
            """;
        var result = _redactor.Redact(input);

        Assert.Contains("public class Config", result.Content);
        Assert.Contains("Timeout = 30", result.Content);
        Assert.Contains("[REDACTED:aws-access-key]", result.Content);
    }

    [Fact]
    public void Redact_DisabledPattern_ReturnsZeroCounts()
    {
        var result = _redactor.Redact("public int Count = 42;");
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public void Redact_SecretInsideCodeLiteral_IncrementsCodeLiteralCount()
    {
        const string input = "var key = \"AKIAIOSFODNN7EXAMPLE\";";
        var result = _redactor.Redact(input, classifyCodeLiterals: true);

        Assert.True(result.TotalCount >= 1);
        Assert.True(result.CodeLiteralRedactions >= 1);
    }

    [Fact]
    public void Redact_WithoutClassification_ReportsZeroCodeLiteralModifications()
    {
        // A config-file redaction (classification off) does not count as a code-literal modification.
        const string input = "var key = \"AKIAIOSFODNN7EXAMPLE\";";
        var result = _redactor.Redact(input, classifyCodeLiterals: false);

        Assert.True(result.TotalCount >= 1);
        Assert.Equal(0, result.CodeLiteralRedactions);
    }

    [Fact]
    public void Redact_ConnectionStringLiteralInCode_IsClassifiedAsCodeLiteral()
    {
        const string input = "var cs = \"Server=db;Database=app;User ID=sa;Password=secret\";";
        var result = _redactor.Redact(input, classifyCodeLiterals: true);

        Assert.Equal(1, result.CountsByKind["connection-string"]);
        Assert.Equal(1, result.CodeLiteralRedactions);
    }

    [Fact]
    public void Redact_CodeLiteralCount_ExcludedFromTotalCount()
    {
        const string input = "var key = \"AKIAIOSFODNN7EXAMPLE\";";
        var classified = _redactor.Redact(input, classifyCodeLiterals: true);
        var plain = _redactor.Redact(input, classifyCodeLiterals: false);

        // The classification must not inflate the secret total.
        Assert.Equal(plain.TotalCount, classified.TotalCount);
    }

    [Fact]
    public void Redact_SampleShopCSharpFiles_ProduceNoConnectionStringFalsePositives()
    {
        // Fidelity guard: real connection strings in SampleShop live in appsettings.json, never in .cs code.
        // Reducing every .cs source file must therefore yield zero connection-string redactions, proving the
        // tightened pattern does not mutate code bodies (a change `verify` cannot see).
        var sampleShop = Path.Combine(RepoRoot(), "tests", "fixtures", "SampleShop");
        var sources = Directory.EnumerateFiles(sampleShop, "*.cs", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(sources);

        foreach (var file in sources)
        {
            var result = _redactor.Redact(File.ReadAllText(file));
            result.CountsByKind.TryGetValue("connection-string", out var count);
            Assert.True(count == 0, $"Connection-string false positive in {file}");
        }
    }

    private static string RepoRoot()
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Fuse.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root containing Fuse.slnx.");
    }
}
