using Fuse.Dotnet;

namespace Fuse.Evals;

/// <summary>
///     Creates a small multi-project repository for the evals: two libraries (one depending on the other), a console
///     app, and two xUnit test projects, committed to git, restored and built.
/// </summary>
internal static class FixtureGenerator
{
    public static async Task<string> CreateAsync(string directory)
    {
        if (Directory.Exists(Path.Combine(directory, ".git")) && File.Exists(Path.Combine(directory, "Fixture.sln")))
            return directory;
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);

        async Task Run(string file, params string[] args)
        {
            var result = await ProcessRunner.RunAsync(file, args, directory, CancellationToken.None);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"{file} {string.Join(' ', args)} failed:\n{result.Output}");
        }

        // The fixture lives inside the Fuse repository; these files stop MSBuild from importing Fuse's own build settings.
        await File.WriteAllTextAsync(Path.Combine(directory, "global.json"), """{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }""");
        await File.WriteAllTextAsync(Path.Combine(directory, "Directory.Build.props"), "<Project />");
        await File.WriteAllTextAsync(Path.Combine(directory, "Directory.Build.targets"), "<Project />");
        await File.WriteAllTextAsync(
            Path.Combine(directory, "Directory.Packages.props"),
            "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        await Run("git", "init", "-q");
        await Run("dotnet", "new", "sln", "-n", "Fixture", "--format", "sln");
        await Run("dotnet", "new", "classlib", "-n", "Core", "-o", "src/Core", "-f", "net10.0");
        await Run("dotnet", "new", "classlib", "-n", "Services", "-o", "src/Services", "-f", "net10.0");
        await Run("dotnet", "new", "console", "-n", "App", "-o", "src/App", "-f", "net10.0");
        await Run("dotnet", "new", "xunit", "-n", "Core.Tests", "-o", "tests/Core.Tests", "-f", "net10.0");
        await Run("dotnet", "new", "xunit", "-n", "Services.Tests", "-o", "tests/Services.Tests", "-f", "net10.0");
        await Run("dotnet", "add", "src/Services", "reference", "src/Core");
        await Run("dotnet", "add", "src/App", "reference", "src/Services");
        await Run("dotnet", "add", "tests/Core.Tests", "reference", "src/Core");
        await Run("dotnet", "add", "tests/Services.Tests", "reference", "src/Services");
        await Run("dotnet", "sln", "Fixture.sln", "add", "src/Core", "src/Services", "src/App", "tests/Core.Tests", "tests/Services.Tests");

        foreach (var generated in new[] { "src/Core/Class1.cs", "src/Services/Class1.cs", "tests/Core.Tests/UnitTest1.cs", "tests/Services.Tests/UnitTest1.cs" })
            File.Delete(Path.Combine(directory, generated));
        foreach (var (path, content) in Sources)
            await File.WriteAllTextAsync(Path.Combine(directory, path), content);
        await File.WriteAllTextAsync(Path.Combine(directory, ".gitignore"), "bin/\nobj/\n");

        await Run("git", "add", "-A");
        await Run("git", "-c", "user.email=evals@fuse.local", "-c", "user.name=fuse evals", "commit", "-qm", "fixture");
        await Run("dotnet", "build", "Fixture.sln", "-nologo", "-v:q");
        return directory;
    }

    private static readonly (string Path, string Content)[] Sources =
    [
        ("src/Core/Money.cs", """
            namespace Core;

            public readonly record struct Money(decimal Amount, string Currency)
            {
                public static Money Zero(string currency) => new(0m, currency);

                public Money Add(Money other)
                {
                    if (other.Currency != Currency)
                        throw new InvalidOperationException("currency mismatch");
                    return new Money(Amount + other.Amount, Currency);
                }

                public Money Multiply(int factor) => new(Amount * factor, Currency);

                public bool IsZero() => Amount == 0m;

                public bool IsGreaterThan(Money other) => Amount > other.Amount;
            }
            """),
        ("src/Core/Geometry.cs", """
            namespace Core;

            public static class Geometry
            {
                public static int Area(int width, int height) => width * height;

                public static int Perimeter(int width, int height)
                {
                    var sides = 2;
                    return sides * (width + height);
                }

                public static bool IsSquare(int width, int height)
                {
                    if (width <= 0 || height <= 0)
                        return false;
                    return width == height;
                }
            }
            """),
        ("src/Core/TextTools.cs", """
            using System.Text;

            namespace Core;

            public static class TextTools
            {
                public static string Slugify(string text)
                {
                    var builder = new StringBuilder();
                    foreach (var c in text.Trim().ToLowerInvariant())
                    {
                        if (char.IsLetterOrDigit(c))
                            builder.Append(c);
                        else if (builder.Length > 0 && builder[^1] != '-')
                            builder.Append('-');
                    }

                    return builder.ToString().TrimEnd('-');
                }

                public static int WordCount(string text) =>
                    text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

                public static string Truncate(string text, int max)
                {
                    if (text.Length <= max)
                        return text;
                    return text[..max] + "...";
                }
            }
            """),
        ("src/Core/Clock.cs", """
            namespace Core;

            public interface IClock
            {
                DateTime Now { get; }
            }

            public sealed class FixedClock(DateTime now) : IClock
            {
                public DateTime Now { get; } = now;
            }
            """),
        ("src/Services/OrderService.cs", """
            using Core;

            namespace Services;

            public sealed class OrderService(IClock clock)
            {
                private readonly List<Money> _lines = [];

                public void AddLine(Money price, int quantity)
                {
                    if (quantity <= 0)
                        throw new ArgumentOutOfRangeException(nameof(quantity));
                    _lines.Add(price.Multiply(quantity));
                }

                public Money Total()
                {
                    var total = Money.Zero("EUR");
                    foreach (var line in _lines)
                        total = total.Add(line);
                    return total;
                }

                public bool IsLarge() => Total().Amount >= 100m;

                public bool IsWeekend() => clock.Now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

                public Money Discounted(int percent)
                {
                    var total = Total();
                    return new Money(total.Amount * (100 - percent) / 100, total.Currency);
                }
            }
            """),
        ("src/Services/ReportBuilder.cs", """
            using Core;

            namespace Services;

            public static class ReportBuilder
            {
                public static string Title(string name) => TextTools.Truncate(TextTools.Slugify(name), 20);

                public static string Describe(int width, int height)
                {
                    var kind = Geometry.IsSquare(width, height) ? "square" : "rectangle";
                    return $"{kind} {Geometry.Area(width, height)}";
                }
            }
            """),
        ("src/App/Program.cs", """
            using Core;
            using Services;

            var orders = new OrderService(new FixedClock(new DateTime(2026, 1, 3)));
            orders.AddLine(new Money(10m, "EUR"), 3);
            Console.WriteLine(orders.Total());
            Console.WriteLine(ReportBuilder.Title("Hello World"));
            Console.WriteLine(ReportBuilder.Describe(2, 2));
            """),
        ("tests/Core.Tests/MoneyTests.cs", """
            using Core;

            namespace Core.Tests;

            public class MoneyTests
            {
                [Fact] public void Adds() => Assert.Equal(new Money(3m, "EUR"), new Money(1m, "EUR").Add(new Money(2m, "EUR")));
                [Fact] public void Multiplies() => Assert.Equal(new Money(6m, "EUR"), new Money(2m, "EUR").Multiply(3));
                [Fact] public void ZeroIsZero() => Assert.True(Money.Zero("EUR").IsZero());
                [Fact] public void Compares() => Assert.True(new Money(5m, "EUR").IsGreaterThan(new Money(4m, "EUR")));
                [Fact] public void RejectsMixedCurrencies() => Assert.Throws<InvalidOperationException>(() => new Money(1m, "EUR").Add(new Money(1m, "USD")));
            }
            """),
        ("tests/Core.Tests/GeometryTests.cs", """
            using Core;

            namespace Core.Tests;

            public class GeometryTests
            {
                [Fact] public void Area() => Assert.Equal(12, Geometry.Area(3, 4));
                [Fact] public void Perimeter() => Assert.Equal(14, Geometry.Perimeter(3, 4));
                [Fact] public void Square() => Assert.True(Geometry.IsSquare(2, 2));
                [Fact] public void NotSquare() => Assert.False(Geometry.IsSquare(2, 3));
                [Fact] public void NegativeIsNotSquare() => Assert.False(Geometry.IsSquare(-1, -1));
            }
            """),
        ("tests/Core.Tests/TextToolsTests.cs", """
            using Core;

            namespace Core.Tests;

            public class TextToolsTests
            {
                [Fact] public void Slugifies() => Assert.Equal("hello-world", TextTools.Slugify(" Hello, World! "));
                [Fact] public void CountsWords() => Assert.Equal(3, TextTools.WordCount("a b  c"));
                [Fact] public void Truncates() => Assert.Equal("abc...", TextTools.Truncate("abcdef", 3));
                [Fact] public void KeepsShort() => Assert.Equal("ab", TextTools.Truncate("ab", 3));
            }
            """),
        ("tests/Services.Tests/OrderServiceTests.cs", """
            using Core;
            using Services;

            namespace Services.Tests;

            public class OrderServiceTests
            {
                private static OrderService Create(int day = 5) => new(new FixedClock(new DateTime(2026, 1, day)));

                [Fact]
                public void Totals()
                {
                    var orders = Create();
                    orders.AddLine(new Money(10m, "EUR"), 3);
                    Assert.Equal(30m, orders.Total().Amount);
                }

                [Fact]
                public void Large()
                {
                    var orders = Create();
                    orders.AddLine(new Money(50m, "EUR"), 2);
                    Assert.True(orders.IsLarge());
                }

                [Fact] public void Weekend() => Assert.True(Create(3).IsWeekend());
                [Fact] public void RejectsZeroQuantity() => Assert.Throws<ArgumentOutOfRangeException>(() => Create().AddLine(new Money(1m, "EUR"), 0));

                [Fact]
                public void Discounts()
                {
                    var orders = Create();
                    orders.AddLine(new Money(100m, "EUR"), 1);
                    Assert.Equal(90m, orders.Discounted(10).Amount);
                }
            }
            """),
        ("tests/Services.Tests/ReportBuilderTests.cs", """
            using Services;

            namespace Services.Tests;

            public class ReportBuilderTests
            {
                [Fact] public void Title() => Assert.Equal("hello-world", ReportBuilder.Title("Hello World"));
                [Fact] public void DescribesSquare() => Assert.Equal("square 4", ReportBuilder.Describe(2, 2));
                [Fact] public void DescribesRectangle() => Assert.Equal("rectangle 6", ReportBuilder.Describe(2, 3));
            }
            """),
    ];
}
