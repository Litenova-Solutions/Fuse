using System.Diagnostics;
using Fuse.Tests.Fixtures;
namespace Fuse.Tests.EndToEnd;

/// <summary>A .NET 11 repository may use C# 15: a union type checks clean, and a genuine error in the same file is still reported.</summary>
public class Net11LanguageTests
{
    [Fact]
    public async Task A_union_type_checks_clean_and_a_genuine_error_is_reported()
    {
        Assert.SkipUnless(HasNet11Sdk(), "the .NET 11 SDK is not installed");
        using var repo = CreateNet11Repo();
        try
        {
            repo.Write("Lib/Pets.cs", "namespace Lib;\n\npublic union Pet(Cat, Dog);\n");

            var clean = await FuseProcess.RunAsync(repo.Path, null, "check");
            Assert.Equal(0, clean.ExitCode);
            Assert.Contains("fuse: no errors introduced", clean.Stdout, StringComparison.Ordinal);

            repo.Write("Lib/Pets.cs", "namespace Lib;\n\npublic union Pet(Cat, Dog);\n\npublic static class Broken\n{\n    public static int Value() => undefinedName;\n}\n");

            var broken = await FuseProcess.RunAsync(repo.Path, null, "check");
            Assert.Equal(1, broken.ExitCode);
            Assert.Contains("Lib/Pets.cs", broken.Stdout, StringComparison.Ordinal);
            Assert.Contains("error CS0103", broken.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    /// <summary>A committed net11.0 classlib with its two union members, restored, so the union itself is the uncommitted change.</summary>
    private static FixtureRepo CreateNet11Repo()
    {
        var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            [".gitignore"] = "bin/\nobj/\n",
            ["global.json"] = "{ \"sdk\": { \"version\": \"11.0.100\", \"rollForward\": \"latestMajor\" } }\n",
            ["Net11.slnx"] = "<Solution>\n  <Project Path=\"Lib/Lib.csproj\" />\n</Solution>\n",
            ["Lib/Lib.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """,
            ["Lib/Animals.cs"] = """
                namespace Lib;

                public sealed class Cat
                {
                    public string Name { get; init; } = "";
                }

                public sealed class Dog
                {
                    public string Name { get; init; } = "";
                }
                """,
        });
        FixtureRepo.Run(repo.Root.Path, "dotnet", "restore", "Net11.slnx", "-nologo", "-v:q");
        return repo;
    }

    private static bool HasNet11Sdk()
    {
        try
        {
            var start = new ProcessStartInfo("dotnet", "--list-sdks")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(start);
            if (process is null)
                return false;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(TimeSpan.FromSeconds(30));
            return output.Split('\n').Any(line => line.StartsWith("11.", StringComparison.Ordinal));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
