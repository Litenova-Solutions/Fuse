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
