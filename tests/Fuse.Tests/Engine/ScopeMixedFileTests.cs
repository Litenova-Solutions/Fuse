using Fuse.Check.Model;
using Fuse.Failures;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     A scoped check that mixes a checkable file with a spec it cannot check refuses the spec instead of staying
///     silent about it while claiming its coverage.
/// </summary>
public class ScopeMixedFileTests
{
    [Fact]
    public async Task A_mixed_scope_with_a_file_it_cannot_check_is_refused()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        await engine.SyncAsync("Lib/Calc.cs");

        var refused = await Assert.ThrowsAsync<FuseException>(() => CheckPathsAsync(engine, engine.Repo.Full("Lib/Calc.cs"), engine.Repo.Full("notes.md")));

        Assert.Equal(ErrorCode.InvalidPath, refused.Code);
        Assert.Contains("notes.md", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mixed_scope_with_a_file_no_project_owns_is_refused()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        engine.Repo.Write("Unowned.cs", "public static class Unowned\n{\n}\n");
        await engine.SyncAsync("Lib/Calc.cs");

        var refused = await Assert.ThrowsAsync<FuseException>(() => CheckPathsAsync(engine, engine.Repo.Full("Lib/Calc.cs"), engine.Repo.Full("Unowned.cs")));

        Assert.Equal(ErrorCode.InvalidPath, refused.Code);
        Assert.Contains("Unowned.cs", refused.Message, StringComparison.Ordinal);
    }

    private static Task<CheckResult> CheckPathsAsync(InProcessEngine engine, params string[] specs) =>
        engine.Checker.CheckAsync(new CheckScope.NamedPaths(specs), PhaseTimes.None, TestContext.Current.CancellationToken);
}
