using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     Every check records how long each phase took, because the engine writes one phase line per request and the evals
///     read it back to say where a check spends its time.
/// </summary>
public class PhaseTimingTests
{
    [Fact]
    public async Task A_check_times_its_phases()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * undefinedValue;");

        var (result, phases) = await engine.CheckWithPhasesAsync("Lib/Calc.cs");

        // A body-only edit reaches no other file, so the check ends after the declaration diff and binds no candidates.
        Assert.Equal(["sync", "load", "bindTargets", "surfaceDiff"], phases.Select(p => p.Phase));
        Assert.All(phases, p => Assert.True(p.Ms >= 0, $"{p.Phase} was {p.Ms}"));
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task A_check_that_reaches_dependents_times_the_search_and_the_candidates()
    {
        await using var engine = await EngineHarness.StartAsync();
        // Renaming a declaration sends the check past the target files into the projects that use it.
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int AddRenamed(");

        var (result, phases) = await engine.CheckWithPhasesAsync("Lib/Calc.cs");

        Assert.Equal(["sync", "load", "bindTargets", "surfaceDiff", "load", "referenceSearch", "bindCandidates"], phases.Select(p => p.Phase));
        Assert.All(phases, p => Assert.True(p.Ms >= 0, $"{p.Phase} was {p.Ms}"));
        Assert.True(result.DependentProjectsChecked > 0, $"checked {result.DependentProjectsChecked} dependent project(s)");
    }
}
