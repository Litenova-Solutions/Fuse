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
    public void Redact_ConnectionString_ReplacesCredentials()
    {
        const string input = "Server=myserver;User ID=admin;Password=SecretP@ssw0rd123";
        var result = _redactor.Redact(input);

        Assert.Contains("[REDACTED:connection-string]", result.Content);
        Assert.DoesNotContain("SecretP@ssw0rd123", result.Content);
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
}
