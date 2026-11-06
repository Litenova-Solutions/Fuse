using Fuse.Check.Model;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     Checks that run at the same time, in this process and with no lock between them, answer as each would
///     alone: the same edit gives the same answer to every caller, different files keep their own errors, a
///     declaration change reaches its dependents for every caller, and sessions keep their own answers.
/// </summary>
public class ConcurrentCheckTests
{
    [Fact]
    public async Task Concurrent_checks_of_the_same_file_answer_the_same()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * missingValue;");

        var reports = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => engine.CheckAsync("Lib/Calc.cs")));

        var first = Assert.Single(reports[0].Errors).Error;
        Assert.Equal("CS0103", first.Id);
        Assert.All(reports, report =>
        {
            var error = Assert.Single(report.Errors).Error;
            Assert.Equal(first, error);
            Assert.Equal(reports[0].FilesChecked, report.FilesChecked);
        });
    }

    [Fact]
    public async Task Concurrent_checks_of_different_files_keep_their_own_errors()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * missingCalc;");
        engine.Repo.Replace("Lib/Formatter.cs", "value.ToString(", "missingFormatter.ToString(");

        var (calc, formatter) = (engine.CheckAsync("Lib/Calc.cs"), engine.CheckAsync("Lib/Formatter.cs"));
        var reports = await Task.WhenAll(calc, formatter);

        var calcError = Assert.Single(reports[0].Errors).Error;
        Assert.Equal("Lib/Calc.cs", calcError.Path);
        Assert.Contains("missingCalc", calcError.Message, StringComparison.Ordinal);
        var formatterError = Assert.Single(reports[1].Errors).Error;
        Assert.Equal("Lib/Formatter.cs", formatterError.Path);
        Assert.Contains("missingFormatter", formatterError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_checks_of_a_declaration_change_each_reach_the_dependents()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");

        var reports = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => engine.CheckAsync("Lib/Calc.cs")));

        Assert.All(reports, report =>
        {
            Assert.Equal(
                ["App/Program.cs", "Lib.Tests/CalcTests.cs"],
                report.Errors.Select(e => e.Error.Path).Order(StringComparer.Ordinal));
            Assert.Equal(["Lib"], report.DeclarationsChangedIn);
        });
    }

    [Fact]
    public async Task Concurrent_checks_answered_to_different_sessions_keep_their_own_errors()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * missingCalc;");
        engine.Repo.Replace("Lib/Formatter.cs", "value.ToString(", "missingFormatter.ToString(");

        var (calc, formatter) = (
            engine.CheckAsSessionAsync("claude:a", "Lib/Calc.cs"),
            engine.CheckAsSessionAsync("claude:b", "Lib/Formatter.cs"));
        var reports = await Task.WhenAll(calc, formatter);

        Assert.Equal("Lib/Calc.cs", Assert.Single(reports[0].Errors).Error.Path);
        Assert.Equal("Lib/Formatter.cs", Assert.Single(reports[1].Errors).Error.Path);
    }

    [Fact]
    public async Task Concurrent_checks_of_all_changes_answer_the_same()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * missingValue;");

        var reports = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => engine.CheckAllAsync()));

        Assert.All(reports, report => Assert.Equal("CS0103", Assert.Single(report.Errors).Error.Id));
    }

    public async Task Concurrent_direct_checks_of_the_same_file_answer_the_same()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * missingValue;");
        await engine.SyncAsync("Lib/Calc.cs");

        var reports = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            engine.Checker.CheckAsync(new CheckScope.Files([engine.Repo.PathOf("Lib/Calc.cs")]), PhaseTimes.None, TestContext.Current.CancellationToken)));

        Assert.All(reports, report => Assert.Equal("CS0103", Assert.Single(report.Errors).Error.Id));
    }
}
