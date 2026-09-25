using Fuse.Check;
using Fuse.Cli;
using Fuse.Dotnet;
using Fuse.Engine;
using Fuse.Hooks;
using Fuse.Protocol;
using Fuse.Testing;

namespace Fuse.Tests.Unit;

public class BuildOutputParserTests
{
    [Fact]
    public void Extracts_errors_without_project_tags_and_duplicates()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";
        var a = Path.Combine(root, "src", "A.cs");
        var b = Path.Combine(root, "src", "B.cs");
        var project = Path.Combine(root, "src", "A.csproj");
        var output = $"""
            {a}(12,5): error CS1061: 'X' does not contain a definition for 'Y' [{project}::TargetFramework=net8.0]
            {a}(12,5): error CS1061: 'X' does not contain a definition for 'Y' [{project}::TargetFramework=net10.0]
            {b}(1,1): warning CS0168: The variable 'e' is declared but never used [{project}]
            {project} : error NU1101: Unable to find package Nope. [{project}]
            MSBUILD : error MSB1009: Project file does not exist.
            Build FAILED.
            """;
        var errors = BuildOutputParser.Errors(output, root);
        Assert.Equal(
            [
                "src/A.cs(12,5): error CS1061: 'X' does not contain a definition for 'Y'",
                "src/A.csproj: error NU1101: Unable to find package Nope.",
                "MSBUILD: error MSB1009: Project file does not exist.",
            ],
            errors);
    }
}
