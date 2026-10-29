using System.Text.Json;
using Fuse.Dotnet;
using Fuse.Operations;

namespace Fuse.Tests.Unit;

/// <summary>JSON lines keep quotes and apostrophes as they are: they go to a terminal or a script, never into HTML.</summary>
public class DiagnosticJsonEscapingTests
{
    [Fact]
    public void A_message_with_apostrophes_and_brackets_prints_them_as_they_are()
    {
        var line = DiagnosticJson.BuildLine(new BuildDiagnostic("App/Program.cs", 3, 30, "CS1061", "'Calc<T>' does not contain a definition for 'Add'"));

        Assert.Contains("'Calc<T>' does not contain a definition for 'Add'", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", line, StringComparison.Ordinal);
        using var parsed = JsonDocument.Parse(line);
        Assert.Equal("'Calc<T>' does not contain a definition for 'Add'", parsed.RootElement.GetProperty("message").GetString());
    }
}
