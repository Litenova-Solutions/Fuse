using Fuse.Check.Model;
using Fuse.Failures;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     A check scoped to directories and patterns: it reports the slice's own errors and what the slice breaks outside
///     it, with the cause that reached each outside file, and nothing from another slice. A scope that matches nothing
///     checkable is refused instead of answering clean.
/// </summary>
public class ScopedCheckTests
{
    [Fact]
    public async Task A_directory_scope_reports_its_errors_and_what_they_break_outside_it()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        engine.Repo.Replace("Strict/Thing.cs", "public static int Value() => 1;", "public static int Value() => missing;");
        await engine.SyncAsync("Lib/Calc.cs", "Strict/Thing.cs");

        var scoped = await CheckPathsAsync(engine, engine.Repo.Full("Lib"));

        Assert.Equal(["App/Program.cs", "Lib.Tests/CalcTests.cs"], scoped.Errors.Select(e => e.Error.Path).Order(StringComparer.Ordinal));
        Assert.All(scoped.Errors, e => Assert.Equal("CS1061", e.Error.Id));
        Assert.NotNull(Assert.Single(scoped.Errors, e => e.Error.Path == "App/Program.cs").Cause);
        Assert.DoesNotContain(scoped.Errors, e => e.Error.Path == "Strict/Thing.cs");
        Assert.Equal(["Lib"], scoped.ScopedPaths);
    }

    [Fact]
    public async Task A_glob_scope_equals_the_same_files_named_one_by_one()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        await engine.SyncAsync("Lib/Calc.cs");

        var glob = await CheckPathsAsync(engine, Path.Combine(engine.Repo.Root.Path, "Lib", "**", "*.cs"));
        var files = await engine.CheckAsync("Lib/Calc.cs");

        Assert.Equal(files.Errors.Select(e => e.Error.ToString()).Order(StringComparer.Ordinal), glob.Errors.Select(e => e.Error.ToString()).Order(StringComparer.Ordinal));
        Assert.Equal(["Lib/**/*.cs"], glob.ScopedPaths);
    }

    [Fact]
    public async Task A_file_scope_mixes_with_a_directory_scope()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        engine.Repo.Replace("Strict/Thing.cs", "public static int Value() => 1;", "public static int Value() => missing;");
        await engine.SyncAsync("Lib/Calc.cs", "Strict/Thing.cs");

        var scoped = await CheckPathsAsync(engine, engine.Repo.Full("Lib"), engine.Repo.Full("Strict/Thing.cs"));

        Assert.Contains(scoped.Errors, e => e.Error.Path == "Strict/Thing.cs");
        Assert.Contains(scoped.Errors, e => e.Error.Path == "App/Program.cs");
    }

    [Fact]
    public async Task A_glob_matching_nothing_is_refused()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        await engine.SyncAsync("Lib/Calc.cs");

        var refused = await Assert.ThrowsAsync<FuseException>(() => CheckPathsAsync(engine, Path.Combine(engine.Repo.Root.Path, "docs", "**")));

        Assert.Equal(ErrorCode.InvalidPath, refused.Code);
        Assert.Contains("docs/**", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_directory_with_no_changed_sources_is_refused()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        await engine.SyncAsync("Lib/Calc.cs");

        var refused = await Assert.ThrowsAsync<FuseException>(() => CheckPathsAsync(engine, engine.Repo.Full("Strict")));

        Assert.Equal(ErrorCode.InvalidPath, refused.Code);
        Assert.Contains("Strict", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expansion_past_two_thousand_files_is_refused()
    {
        await using var engine = await InProcessEngine.StartAsync();
        for (var i = 0; i < 2001; i++)
            engine.Repo.Write($"Lib/Generated/File{i}.cs", $"namespace Lib;\n\npublic static class File{i}\n{{\n    public static int Value() => {i};\n}}\n");
        await engine.SyncAsync(Enumerable.Range(0, 2001).Select(i => $"Lib/Generated/File{i}.cs").ToArray());

        var refused = await Assert.ThrowsAsync<FuseException>(() => CheckPathsAsync(engine, engine.Repo.Full("Lib/Generated")));

        Assert.Equal(ErrorCode.InvalidPath, refused.Code);
        Assert.Contains("2000", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_deleted_file_under_a_directory_still_expands()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Delete("Lib/Formatter.cs");
        await engine.SyncAsync("Lib/Formatter.cs");

        var scoped = await CheckPathsAsync(engine, engine.Repo.Full("Lib"));

        Assert.Equal("App/Report.cs", Assert.Single(scoped.Errors).Error.Path);
    }

    private static Task<CheckResult> CheckPathsAsync(InProcessEngine engine, params string[] specs) =>
        engine.Checker.CheckAsync(new CheckScope.NamedPaths(specs), PhaseTimes.None, TestContext.Current.CancellationToken);
}
