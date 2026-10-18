namespace Fuse.Tests.Fixtures;

/// <summary>
///     The standard fixture repository, written, committed, restored and built once per test run. It uses only
///     packages the SDK templates use, so restoring it needs no network once they are cached.
/// </summary>
/// <remarks>
///     Layout:
///     <list type="bullet">
///         <item><c>Lib</c>: <c>Calc</c>, <c>IGreeter</c>/<c>Greeter</c>, <c>Formatter</c>.</item>
///         <item><c>App</c>: an executable referencing Lib, with top-level statements and <c>Report</c>.</item>
///         <item><c>Lib.Tests</c>: xUnit tests of Calc (with a shared helper) and of Greeter through its interface.</item>
///         <item><c>App.Tests</c>: xUnit tests of <c>Report</c>.</item>
///         <item><c>Multi</c>: a library built for net8.0 and net10.0.</item>
///         <item><c>Strict</c>: TreatWarningsAsErrors plus an editorconfig raising CA1825 to error.</item>
///     </list>
/// </remarks>
internal static class FixtureTemplate
{
    public const string SolutionFile = "Fixture.slnx";

    public static readonly Lazy<string> Standard = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    private const string TestPackages = """
          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
            <PackageReference Include="xunit" Version="2.9.3" />
            <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
          </ItemGroup>
          <ItemGroup>
            <Using Include="Xunit" />
          </ItemGroup>
        """;

    public static readonly IReadOnlyDictionary<string, string> Files = new Dictionary<string, string>
    {
        [".gitignore"] = "bin/\nobj/\n",
        [SolutionFile] = """
            <Solution>
              <Project Path="Lib/Lib.csproj" />
              <Project Path="App/App.csproj" />
              <Project Path="Lib.Tests/Lib.Tests.csproj" />
              <Project Path="App.Tests/App.Tests.csproj" />
              <Project Path="Multi/Multi.csproj" />
              <Project Path="Strict/Strict.csproj" />
            </Solution>
            """,
        ["Lib/Lib.csproj"] = Project("<TargetFramework>net10.0</TargetFramework>"),
        ["Lib/Calc.cs"] = """
            namespace Lib;

            public class Calc
            {
                public int Add(int a, int b) => a + b;

                public int Mul(int a, int b) => a * b;
            }
            """,
        ["Lib/Greeting.cs"] = """
            namespace Lib;

            public interface IGreeter
            {
                string Greet(string name);
            }

            public sealed class Greeter : IGreeter
            {
                public string Greet(string name) => "Hello " + name;
            }
            """,
        ["Lib/Formatter.cs"] = """
            namespace Lib;

            public static class Formatter
            {
                public static string Format(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            """,
        ["App/App.csproj"] = Project("<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>", "<ProjectReference Include=\"../Lib/Lib.csproj\" />"),
        ["App/Program.cs"] = """
            var calc = new Lib.Calc();
            Lib.IGreeter greeter = new Lib.Greeter();
            System.Console.WriteLine(greeter.Greet("x") + calc.Add(1, 2));
            """,
        ["App/Report.cs"] = """
            namespace App;

            public static class Report
            {
                public static string Line(int value) => "value=" + Lib.Formatter.Format(value);
            }
            """,
        ["Lib.Tests/Lib.Tests.csproj"] = Project("<TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable>", "<ProjectReference Include=\"../Lib/Lib.csproj\" />", TestPackages),
        ["Lib.Tests/CalcTests.cs"] = """
            using Lib;

            namespace Lib.Tests;

            public class CalcTests
            {
                [Fact]
                public void Adds() => Assert.Equal(3, NewCalc().Add(1, 2));

                [Fact]
                public void Multiplies() => Assert.Equal(6, NewCalc().Mul(2, 3));

                private static Calc NewCalc() => new();
            }
            """,
        ["Lib.Tests/GreeterTests.cs"] = """
            using Lib;

            namespace Lib.Tests;

            public class GreeterTests
            {
                [Fact]
                public void Greets()
                {
                    IGreeter greeter = new Greeter();
                    Assert.Equal("Hello Ann", greeter.Greet("Ann"));
                }
            }
            """,
        ["App.Tests/App.Tests.csproj"] = Project("<TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable>", "<ProjectReference Include=\"../App/App.csproj\" />", TestPackages),
        ["App.Tests/ReportTests.cs"] = """
            namespace App.Tests;

            public class ReportTests
            {
                [Fact]
                public void Formats() => Assert.Equal("value=5", App.Report.Line(5));
            }
            """,
        ["Multi/Multi.csproj"] = Project("<TargetFrameworks>net8.0;net10.0</TargetFrameworks>"),
        ["Multi/Shape.cs"] = """
            namespace Multi;

            public static class Shape
            {
                public static int Sides(int count) => count;
            }
            """,
        ["Strict/Strict.csproj"] = Project("<TargetFramework>net10.0</TargetFramework><TreatWarningsAsErrors>true</TreatWarningsAsErrors>"),
        ["Strict/.editorconfig"] = """
            [*.cs]
            dotnet_diagnostic.CA1825.severity = error
            """,
        ["Strict/Thing.cs"] = """
            namespace Strict;

            public static class Thing
            {
                public static int Value() => 1;
            }
            """,
    };

    public const string PlatformSolutionFile = "Platform.slnx";

    private const string XUnitV3Packages = """
        <ItemGroup>
          <PackageReference Include="xunit.v3" Version="3.2.2" />
        </ItemGroup>
        <ItemGroup>
          <Using Include="Xunit" />
        </ItemGroup>
        """;

    private const string MSTestPackages = """
        <ItemGroup>
          <PackageReference Include="MSTest" Version="4.4.0" />
        </ItemGroup>
        """;

    private const string NUnitPackages = """
        <ItemGroup>
          <PackageReference Include="NUnit" Version="4.6.0" />
          <PackageReference Include="NUnit3TestAdapter" Version="6.3.0" />
        </ItemGroup>
        """;

    private const string TUnitPackages = """
        <ItemGroup>
          <PackageReference Include="TUnit" Version="1.0.0" />
        </ItemGroup>
        """;

    private const string XUnitV3Tests = """
        using Lib;

        namespace Platform.Tests;

        public class CalcTests
        {
            [Fact]
            public void Adds() => Assert.Equal(3, new Calc().Add(1, 2));

            [Fact]
            public void Multiplies() => Assert.Equal(6, new Calc().Mul(2, 3));
        }
        """;

    private const string MSTestTests = """
        using Microsoft.VisualStudio.TestTools.UnitTesting;

        namespace Platform.Tests;

        [TestClass]
        public class CalcTests
        {
            [TestMethod]
            public void Adds() => Assert.AreEqual(3, new Lib.Calc().Add(1, 2));

            [TestMethod]
            public void Multiplies() => Assert.AreEqual(6, new Lib.Calc().Mul(2, 3));
        }
        """;

    private const string NUnitTests = """
        using NUnit.Framework;

        namespace Platform.Tests;

        public class CalcTests
        {
            [Test]
            public void Adds() => Assert.That(new Lib.Calc().Add(1, 2), Is.EqualTo(3));

            [Test]
            public void Multiplies() => Assert.That(new Lib.Calc().Mul(2, 3), Is.EqualTo(6));
        }
        """;

    private const string TUnitTests = """
        using TUnit.Assertions;
        using TUnit.Assertions.Extensions;
        using TUnit.Core;

        namespace Platform.Tests;

        public class CalcTests
        {
            [Test]
            public async Task Adds() => await Assert.That(new Lib.Calc().Add(1, 2)).IsEqualTo(3);

            [Test]
            public async Task Multiplies() => await Assert.That(new Lib.Calc().Mul(2, 3)).IsEqualTo(6);
        }
        """;

    /// <summary>
    ///     A minimal repository with <c>Lib</c> and one Microsoft.Testing.Platform test project for
    ///     <paramref name="framework"/> (<c>xunit-v3</c>, <c>mstest</c>, <c>nunit</c> or <c>tunit</c>), with the
    ///     <c>global.json</c> opt-in only when <paramref name="optIn"/> is true.
    /// </summary>
    public static IReadOnlyDictionary<string, string> PlatformRepo(string framework, bool optIn)
    {
        var files = new Dictionary<string, string>
        {
            [".gitignore"] = "bin/\nobj/\n",
            [PlatformSolutionFile] = """
                <Solution>
                  <Project Path="Lib/Lib.csproj" />
                  <Project Path="Tests/Tests.csproj" />
                </Solution>
                """,
            ["Lib/Lib.csproj"] = Project("<TargetFramework>net10.0</TargetFramework>"),
            ["Lib/Calc.cs"] = """
                namespace Lib;

                public class Calc
                {
                    public int Add(int a, int b) => a + b;

                    public int Mul(int a, int b) => a * b;
                }
                """,
            ["Tests/Tests.csproj"] = PlatformTestProject(framework),
            ["Tests/CalcTests.cs"] = PlatformTests(framework),
        };
        if (optIn)
            files["global.json"] = """{ "test": { "runner": "Microsoft.Testing.Platform" } }""";
        return files;
    }

    private static string PlatformTestProject(string framework) => framework switch
    {
        "xunit-v3" => Project(
            "<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable>",
            "<ProjectReference Include=\"../Lib/Lib.csproj\" />",
            XUnitV3Packages),
        "mstest" => Project(
            "<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable><EnableMSTestRunner>true</EnableMSTestRunner>",
            "<ProjectReference Include=\"../Lib/Lib.csproj\" />",
            MSTestPackages),
        "nunit" => Project(
            "<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable><EnableNUnitRunner>true</EnableNUnitRunner>",
            "<ProjectReference Include=\"../Lib/Lib.csproj\" />",
            NUnitPackages),
        "tunit" => Project(
            "<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable>",
            "<ProjectReference Include=\"../Lib/Lib.csproj\" />",
            TUnitPackages),
        _ => throw new ArgumentOutOfRangeException(nameof(framework), framework, null),
    };

    private static string PlatformTests(string framework) => framework switch
    {
        "xunit-v3" => XUnitV3Tests,
        "mstest" => MSTestTests,
        "nunit" => NUnitTests,
        "tunit" => TUnitTests,
        _ => throw new ArgumentOutOfRangeException(nameof(framework), framework, null),
    };

    private static string Project(string properties, string references = "", string extra = "") => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            {properties}
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
          <ItemGroup>
            {references}
          </ItemGroup>
        {extra}
        </Project>
        """;

    private static string Create()
    {
        var directory = FixtureRepo.NewDirectory();
        foreach (var (relative, content) in Files)
        {
            var path = Path.Combine(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, FixtureRepo.Lf(content));
        }

        FixtureRepo.GitInit(directory);
        FixtureRepo.Run(directory, "dotnet", "build", SolutionFile, "-nologo", "-v:q");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        };
        return directory;
    }
}
