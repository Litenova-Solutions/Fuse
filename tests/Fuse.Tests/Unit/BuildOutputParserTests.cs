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
        const string output = """
            C:\repo\src\A.cs(12,5): error CS1061: 'X' does not contain a definition for 'Y' [C:\repo\src\A.csproj::TargetFramework=net8.0]
            C:\repo\src\A.cs(12,5): error CS1061: 'X' does not contain a definition for 'Y' [C:\repo\src\A.csproj::TargetFramework=net10.0]
            C:\repo\src\B.cs(1,1): warning CS0168: The variable 'e' is declared but never used [C:\repo\src\A.csproj]
            C:\repo\src\A.csproj : error NU1101: Unable to find package Nope. [C:\repo\src\A.csproj]
            MSBUILD : error MSB1009: Project file does not exist.
            Build FAILED.
            """;
        var errors = BuildOutputParser.Errors(output, @"C:\repo");
        Assert.Equal(
            [
                "src/A.cs(12,5): error CS1061: 'X' does not contain a definition for 'Y'",
                "src/A.csproj: error NU1101: Unable to find package Nope.",
                "MSBUILD: error MSB1009: Project file does not exist.",
            ],
            errors);
    }
}
