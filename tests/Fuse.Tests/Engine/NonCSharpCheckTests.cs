using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>A check names the changed files it does not check, and counts no ignored file.</summary>
public class NonCSharpCheckTests
{
    [Fact]
    public async Task A_mixed_change_names_the_non_csharp_file()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        engine.Repo.Write("notes/app.tsx", "export const x = 1;\n");
        await Task.Delay(400, TestContext.Current.CancellationToken);

        var report = await engine.CheckAllAsync();

        Assert.Empty(report.Errors);
        Assert.Equal(1, report.NonCSharpChanged);
        Assert.Equal(["notes/app.tsx"], report.NonCSharpSample);
    }

    [Fact]
    public async Task Only_non_csharp_changes_still_name_them()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Write("notes/app.tsx", "export const x = 1;\n");
        await Task.Delay(400, TestContext.Current.CancellationToken);

        var report = await engine.CheckAllAsync();

        Assert.Empty(report.Errors);
        Assert.Equal(0, report.FilesChecked);
        Assert.Equal(1, report.NonCSharpChanged);
        Assert.Equal(["notes/app.tsx"], report.NonCSharpSample);
    }

    [Fact]
    public async Task Ignored_files_are_not_counted()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Write("Lib/obj/gen.ts", "export const x = 1;\n");
        await Task.Delay(400, TestContext.Current.CancellationToken);

        var report = await engine.CheckAllAsync();

        Assert.Empty(report.Errors);
        Assert.Empty(engine.Workspace.Tracker.Changed);
        Assert.Equal(0, report.NonCSharpChanged);
        Assert.Empty(report.NonCSharpSample);
    }
}
