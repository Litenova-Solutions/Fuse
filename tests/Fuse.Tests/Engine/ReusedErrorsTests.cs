using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     In a check of every change, a working-tree file whose text and whose project's declarations are as they were at an
///     earlier check keeps the errors that check found for it, so a stop hook that checks many files again after a small
///     fix costs only what changed since. A change to a declaration it can see, in its own project or one it references,
///     makes it bind again. A check of named files, as after an edit, neither reuses nor keeps errors.
/// </summary>
public class ReusedErrorsTests
{
    [Fact]
    public async Task A_file_is_bound_again_when_a_declaration_it_uses_changes_in_another_project()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("App/Report.cs", "\"value=\"", "\"value: \"");
        var first = await engine.CheckAllAsync();
        Assert.Empty(first.Errors);

        // Report.cs is unchanged since the first check; Format, which it calls, is renamed in Lib.
        engine.Repo.Replace("Lib/Formatter.cs", "public static string Format(", "public static string Render(");
        await engine.SyncAsync("Lib/Formatter.cs");
        var second = await engine.CheckAllAsync();

        Assert.Contains(second.Errors, e => e.Error is { Path: "App/Report.cs", Id: "CS0117" });
    }

    [Fact]
    public async Task A_file_keeps_its_errors_when_only_a_method_body_elsewhere_changes()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("App/Report.cs", "Lib.Formatter.Format(value)", "Lib.Formatter.Format(missing)");
        var first = await engine.CheckAllAsync();
        Assert.Contains(first.Errors, e => e.Error is { Path: "App/Report.cs", Id: "CS0103" });

        engine.Repo.Replace("Lib/Formatter.cs", "value.ToString(", "(value + 0).ToString(");
        await engine.SyncAsync("Lib/Formatter.cs");
        engine.Log.Clear();
        var second = await engine.CheckAllAsync();

        Assert.Equal(first.Errors.Select(e => e.Error), second.Errors.Select(e => e.Error));
        Assert.Contains(engine.Log, l => l.StartsWith("check: ", StringComparison.Ordinal) && l.Contains("(1 with errors reused from an earlier check)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_whose_own_text_changed_is_bound_again()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("App/Report.cs", "Lib.Formatter.Format(value)", "Lib.Formatter.Format(missing)");
        await engine.CheckAllAsync();

        engine.Repo.Replace("App/Report.cs", "Format(missing)", "Format(value)");
        var fixedResult = await engine.CheckAllAsync();

        Assert.Empty(fixedResult.Errors);
    }

    [Fact]
    public async Task A_check_of_named_files_reuses_nothing()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("App/Report.cs", "Lib.Formatter.Format(value)", "Lib.Formatter.Format(missing)");
        await engine.CheckAllAsync();

        engine.Log.Clear();
        await engine.CheckAsync("App/Report.cs");

        Assert.DoesNotContain(engine.Log, l => l.Contains("with errors reused from an earlier check", StringComparison.Ordinal));
    }
}
