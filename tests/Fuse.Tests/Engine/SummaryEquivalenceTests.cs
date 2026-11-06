using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     The differential proof for summaries outside the change cone: the same fixture change, checked from
///     source and checked with summaries, gives identical introduced errors and identical reach file sets.
///     Each test also names the answer it expects, so the two engines cannot agree on a wrong answer.
/// </summary>
public class SummaryEquivalenceTests
{
    private const string Vault = "namespace Lib;\n\npublic sealed class Vault\n{\n    public int Open() => Code();\n\n    private int Code() => 7;\n}\n";

    private const string Internal = "namespace Lib;\n\npublic static class Internal\n{\n    internal static int Code() => 7;\n}\n";

    private const string Client = "namespace Lib;\n\npublic class Client\n{\n    public int Old() => 1;\n}\n";

    [Fact]
    public async Task A_body_edit_in_a_dependent_binds_against_a_summary_of_its_dependency()
    {
        await using var pair = await SummaryPair.StartAsync();
        pair.ReplaceBoth("App/Report.cs", "Lib.Formatter.Format(value)", "Lib.Formatter.Missing(value)");

        var (fromSource, withSummaries) = await pair.CheckBothAsync("App/Report.cs");

        Assert.Equal(fromSource, withSummaries);
        Assert.Equal(1, fromSource.ErrorCount);
        Assert.Contains("CS0117", fromSource.Errors, StringComparison.Ordinal);
        Assert.Contains("App/Report.cs", fromSource.Errors, StringComparison.Ordinal);
        Assert.Equal("precise:", fromSource.Reach);
        Assert.True(SummaryPair.SummariesStored(pair.WithSummaries));
        Assert.False(SummaryPair.SummariesStored(pair.FromSource));

        pair.ReplaceBoth("App/Report.cs", "Lib.Formatter.Missing(value)", "Lib.Formatter.Format(value)");
        var (fixedSource, fixedSummaries) = await pair.CheckBothAsync("App/Report.cs");
        Assert.Equal(fixedSource, fixedSummaries);
        Assert.Equal("", fixedSource.Errors);
    }

    [Fact]
    public async Task A_renamed_member_reaches_and_breaks_the_same_files()
    {
        await using var pair = await SummaryPair.StartAsync();
        pair.ReplaceBoth("Lib/Calc.cs", "public int Add(", "public int Plus(");

        var (fromSource, withSummaries) = await pair.CheckBothAsync("Lib/Calc.cs");

        Assert.Equal(fromSource, withSummaries);
        Assert.Equal(2, fromSource.ErrorCount);
        Assert.Contains("CS1061", fromSource.Errors, StringComparison.Ordinal);
        Assert.Equal("Lib", fromSource.DeclarationsChangedIn);
        Assert.Equal("precise:App/Program.cs,Lib.Tests/CalcTests.cs", fromSource.Reach);
    }

    [Fact]
    public async Task A_broad_change_reaches_the_same_files()
    {
        await using var pair = await SummaryPair.StartAsync();
        pair.ReplaceBoth("Lib/Calc.cs", "public class Calc", "public sealed class Calc");

        var (fromSource, withSummaries) = await pair.CheckBothAsync("Lib/Calc.cs");

        Assert.Equal(fromSource, withSummaries);
        Assert.Equal("", fromSource.Errors);
        Assert.StartsWith("broad:", fromSource.Reach, StringComparison.Ordinal);
        Assert.Contains("App/Report.cs", fromSource.Reach, StringComparison.Ordinal);
        Assert.Contains("App.Tests/ReportTests.cs", fromSource.Reach, StringComparison.Ordinal);
        Assert.DoesNotContain("Strict/Thing.cs", fromSource.Reach, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_private_member_of_a_dependency_is_inaccessible_the_same_way()
    {
        await using var pair = await SummaryPair.StartAsync(repo => repo.Write("Lib/Vault.cs", Vault));
        pair.ReplaceBoth("App/Program.cs", "calc.Add(1, 2)", "new Lib.Vault().Code()");

        var (fromSource, withSummaries) = await pair.CheckBothAsync("App/Program.cs");

        Assert.Equal(fromSource, withSummaries);
        Assert.Equal(1, fromSource.ErrorCount);
        Assert.Contains("CS0122", fromSource.Errors, StringComparison.Ordinal);
        Assert.Contains("App/Program.cs", fromSource.Errors, StringComparison.Ordinal);

        pair.ReplaceBoth("App/Program.cs", "new Lib.Vault().Code()", "new Lib.Vault().Open()");
        var (fixedSource, fixedSummaries) = await pair.CheckBothAsync("App/Program.cs");
        Assert.Equal(fixedSource, fixedSummaries);
        Assert.Equal("", fixedSource.Errors);
    }

    [Fact]
    public async Task A_member_made_private_breaks_its_callers_the_same_way()
    {
        await using var pair = await SummaryPair.StartAsync();
        pair.ReplaceBoth("Lib/Calc.cs", "public int Mul(", "private int Mul(");

        var (fromSource, withSummaries) = await pair.CheckBothAsync("Lib/Calc.cs");

        Assert.Equal(fromSource, withSummaries);
        Assert.Equal(1, fromSource.ErrorCount);
        Assert.Contains("CS0122", fromSource.Errors, StringComparison.Ordinal);
        Assert.Contains("Lib.Tests/CalcTests.cs", fromSource.Errors, StringComparison.Ordinal);
        Assert.Equal("precise:Lib.Tests/CalcTests.cs", fromSource.Reach);
    }

    [Fact]
    public async Task An_internal_member_is_visible_to_a_friend_assembly_the_same_way()
    {
        await using var pair = await SummaryPair.StartAsync(repo =>
        {
            repo.Write("Lib/Internal.cs", Internal);
            repo.Write("Lib/AssemblyInfo.cs", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"App\")]\n");
        });
        pair.ReplaceBoth("App/Report.cs", "Lib.Formatter.Format(value)", "Lib.Internal.Code() + value");

        var (fromSource, withSummaries) = await pair.CheckBothAsync("App/Report.cs");

        Assert.Equal(fromSource, withSummaries);
        Assert.Equal("", fromSource.Errors);

        pair.ReplaceBoth("App/Report.cs", "Lib.Internal.Code()", "Lib.Internal.Code(value)");
        var (brokenSource, brokenSummaries) = await pair.CheckBothAsync("App/Report.cs");
        Assert.Equal(brokenSource, brokenSummaries);
        Assert.Equal(1, brokenSource.ErrorCount);
        Assert.Contains("CS1501", brokenSource.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_generated_type_used_by_a_dependent_resolves_the_same_way()
    {
        await using var pair = await SummaryPair.StartAsync(repo =>
        {
            repo.Replace("Lib/Lib.csproj", "</Project>", "<ItemGroup><Compile Include=\"obj/Gen/Client.cs\" /></ItemGroup></Project>");
            repo.Write("Lib/obj/Gen/Client.cs", Client);
        });
        pair.ReplaceBoth("App/Report.cs", "Lib.Formatter.Format(value)", "Lib.Formatter.Format(new Lib.Client().Old())");

        var (fromSource, withSummaries) = await pair.CheckBothAsync("App/Report.cs");

        Assert.Equal(fromSource, withSummaries);
        Assert.Equal("", fromSource.Errors);

        pair.ReplaceBoth("App/Report.cs", "new Lib.Client().Old()", "new Lib.Client().Fresh()");
        var (brokenSource, brokenSummaries) = await pair.CheckBothAsync("App/Report.cs");
        Assert.Equal(brokenSource, brokenSummaries);
        Assert.Equal(1, brokenSource.ErrorCount);
        Assert.Contains("CS1061", brokenSource.Errors, StringComparison.Ordinal);
        Assert.Contains("Lib/obj/Gen/Client.cs", brokenSource.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_summary_whose_inputs_changed_is_not_taken_for_the_changed_HEAD()
    {
        await using var pair = await SummaryPair.StartAsync(repo => repo.Write("Lib/Vault.cs", Vault));
        pair.ReplaceBoth("App/Program.cs", "calc.Add(1, 2)", "new Lib.Vault().Code()");
        var (beforeSource, beforeSummaries) = await pair.CheckBothAsync("App/Program.cs");
        Assert.Equal(beforeSource, beforeSummaries);
        Assert.Contains("CS0122", beforeSource.Errors, StringComparison.Ordinal);

        // The summary of Lib was written for a HEAD whose Vault.Code is private. Making it public and
        // committing only that file moves HEAD, so the check of the same edit binds against the changed
        // surface in both engines and the stored summary for the earlier HEAD answers nothing here.
        pair.ReplaceBoth("Lib/Vault.cs", "private int Code()", "public int Code()");
        pair.CommitFilesBoth("make Code public", "Lib/Vault.cs");
        var (afterSource, afterSummaries) = await pair.CheckBothAsync("App/Program.cs");
        Assert.Equal(afterSource, afterSummaries);
        Assert.Equal("", afterSource.Errors);

        pair.ReplaceBoth("App/Program.cs", "new Lib.Vault().Code()", "new Lib.Vault().Code(1)");
        var (brokenSource, brokenSummaries) = await pair.CheckBothAsync("App/Program.cs");
        Assert.Equal(brokenSource, brokenSummaries);
        Assert.Equal(1, brokenSource.ErrorCount);
        Assert.Contains("CS1501", brokenSource.Errors, StringComparison.Ordinal);
    }
}
