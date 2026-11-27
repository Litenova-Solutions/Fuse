using Fuse.Tests.Fixtures;
namespace Fuse.Tests.EndToEnd;

/// <summary>
///     With two registered sessions, the gate for one is clean while the other's paths hold more broken files than
///     attribution decides, and the gate leaves the working session running.
/// </summary>
public class SessionGateProcessTests
{
    [Fact]
    public async Task A_gate_for_a_registered_session_is_clean_and_leaves_the_session_running()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            // w2 renames Calc.Add with 41 callers of its own: past how many files attribution decides, so without
            // the gate's narrowing the sibling's errors would fail the gate.
            for (var i = 0; i < 41; i++)
                repo.Write($"App/Use{i}.cs", $"public static class Use{i} {{ public static int Run() => new Lib.Calc().Add(1, 2); }}\n");
            repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
            // w1 edits a clean file in its own paths.
            repo.Replace("App/Report.cs", "public static string Line(int value)", "public static string Line(int value) // w1");

            Assert.Equal(0, (await FuseProcess.RunAsync(repo.Path, null, "session", "begin", "w1", "--paths", "App/Report.cs")).ExitCode);
            Assert.Equal(0, (await FuseProcess.RunAsync(repo.Path, null, "session", "begin", "w2", "--paths", "Lib/**", "App/Use*.cs")).ExitCode);

            var gate = await FuseProcess.RunAsync(repo.Path, null, "gate", "--session", "w1");
            Assert.Equal(0, gate.ExitCode);

            // A gate only answers: the working session is never recorded as stopped.
            var sessions = Path.Combine(repo.Root.StateDirectory, "sessions.tsv");
            Assert.False(File.Exists(sessions) && File.ReadAllLines(sessions).Any(line => line == "\tw1"));
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }
}
