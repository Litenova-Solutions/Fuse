using System.Text.Json;
using Fuse.Check.Model;
using Fuse.Dotnet;
using Fuse.Operations;
using Fuse.Protocol;

namespace Fuse.Tests.Unit;

public class DiagnosticJsonTests
{
    [Fact]
    public void A_build_diagnostic_has_exactly_its_five_fields()
    {
        var line = DiagnosticJson.BuildLine(new BuildDiagnostic("App/Program.cs", 3, 30, "CS1061", "'Calc' does not contain a definition for 'Add'"));

        using var parsed = JsonDocument.Parse(line);
        Assert.Equal(5, parsed.RootElement.EnumerateObject().Count());
        Assert.Equal("App/Program.cs", parsed.RootElement.GetProperty("file").GetString());
        Assert.Equal(3, parsed.RootElement.GetProperty("line").GetInt32());
        Assert.Equal(30, parsed.RootElement.GetProperty("column").GetInt32());
        Assert.Equal("CS1061", parsed.RootElement.GetProperty("code").GetString());
        Assert.Equal("'Calc' does not contain a definition for 'Add'", parsed.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void A_general_error_omits_its_line_and_column()
    {
        var line = DiagnosticJson.BuildLine(new BuildDiagnostic("CSC", null, null, "CS5001", "Program does not contain a static Main method"));

        using var parsed = JsonDocument.Parse(line);
        Assert.Equal(3, parsed.RootElement.EnumerateObject().Count());
        Assert.Equal("CSC", parsed.RootElement.GetProperty("file").GetString());
        Assert.Equal("CS5001", parsed.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void A_check_error_adds_its_cause_and_generated_file_when_present()
    {
        var reported = new ReportedError(
            new CompilerError("App/Program.cs", 3, 30, "CS1061", "no Add"),
            new ReportedCause(CauseKind.Removed, "public int Add(int a, int b)"))
        {
            GeneratedFile = "gen/Report.g.cs",
        };

        var line = DiagnosticJson.CheckLine(reported);

        using var parsed = JsonDocument.Parse(line);
        Assert.Equal(7, parsed.RootElement.EnumerateObject().Count());
        Assert.Equal("App/Program.cs", parsed.RootElement.GetProperty("file").GetString());
        Assert.Equal(3, parsed.RootElement.GetProperty("line").GetInt32());
        Assert.Equal(30, parsed.RootElement.GetProperty("column").GetInt32());
        Assert.Equal("removed: public int Add(int a, int b)", parsed.RootElement.GetProperty("cause").GetString());
        Assert.Equal("gen/Report.g.cs", parsed.RootElement.GetProperty("generated").GetString());
    }

    [Fact]
    public void A_check_error_without_cause_or_generated_file_omits_them()
    {
        var reported = new ReportedError(new CompilerError("Lib/Calc.cs", 7, 5, "CS0103", "no x"));

        var line = DiagnosticJson.CheckLine(reported);

        using var parsed = JsonDocument.Parse(line);
        Assert.Equal(5, parsed.RootElement.EnumerateObject().Count());
        Assert.False(parsed.RootElement.TryGetProperty("cause", out _));
        Assert.False(parsed.RootElement.TryGetProperty("generated", out _));
    }
}
