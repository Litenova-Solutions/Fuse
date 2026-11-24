using Fuse.Engine;
using Fuse.Paths;
using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

/// <summary>
///     Frozen session guards: a freeze reports a violation for each changed declaration under a frozen pattern,
///     an unfreeze removes it, the record survives a new instance, and a file outside every pattern reports nothing.
/// </summary>
public class SurfaceGuardStoreTests
{
    [Fact]
    public void A_frozen_pattern_reports_a_violation_for_each_changed_declaration()
    {
        using var repo = FixtureRepo.CreateEmpty(StandardFiles());
        var calc = repo.PathOf("Lib/Calc.cs");
        var store = new SurfaceGuardStore(repo.Root);
        store.Apply(Freeze("contracts", "Lib/**"));

        var events = store.Violations([calc], Declarations(calc, "public static int Add(int a, int b)", "public static int Sub(int a, int b)"));

        Assert.Equal(2, events.Count);
        Assert.Equal("guard_violation", events[0].EventType);
        Assert.Equal("contracts", events[0].GuardName);
        Assert.Equal("Lib/**", events[0].ViolatedPattern);
        Assert.Equal("public static int Add(int a, int b)", events[0].Declaration);
        Assert.Equal("public static int Add(int a, int b) in Lib/Calc.cs changed under frozen pattern Lib/** of contracts", events[0].Message);
        Assert.Equal("public static int Sub(int a, int b)", events[1].Declaration);
        Assert.Equal("guard_violation", events[1].EventType);
    }

    [Fact]
    public void Unfreezing_a_guard_reports_no_violations_and_clears_the_record()
    {
        using var repo = FixtureRepo.CreateEmpty(StandardFiles());
        var calc = repo.PathOf("Lib/Calc.cs");
        var store = new SurfaceGuardStore(repo.Root);
        store.Apply(Freeze("contracts", "Lib/**"));
        store.Apply(Unfreeze("contracts"));

        var events = store.Violations([calc], Declarations(calc, "public static int Add(int a, int b)"));

        Assert.Empty(events);
        Assert.Empty(File.ReadAllLines(Path.Combine(repo.Root.StateDirectory, "guards.tsv")));
    }

    [Fact]
    public void The_record_survives_a_new_instance()
    {
        using var repo = FixtureRepo.CreateEmpty(StandardFiles());
        var calc = repo.PathOf("Lib/Calc.cs");
        new SurfaceGuardStore(repo.Root).Apply(Freeze("contracts", "Lib/**", "App/**"));

        var reloaded = new SurfaceGuardStore(repo.Root);
        var events = reloaded.Violations([calc], Declarations(calc, "public static int Add(int a, int b)"));

        Assert.Single(events);
        Assert.Equal("contracts", events[0].GuardName);
        Assert.Equal("Lib/**", events[0].ViolatedPattern);
        Assert.Equal(["contracts\tApp/**", "contracts\tLib/**"], File.ReadAllLines(Path.Combine(repo.Root.StateDirectory, "guards.tsv")));
    }

    [Fact]
    public void A_file_outside_every_frozen_pattern_reports_nothing()
    {
        using var repo = FixtureRepo.CreateEmpty(StandardFiles());
        var report = repo.PathOf("App/Report.cs");
        var store = new SurfaceGuardStore(repo.Root);
        store.Apply(Freeze("contracts", "Lib/**"));

        Assert.Empty(store.Violations([report], Declarations(report, "public class Report")));
    }

    [Fact]
    public void Freezing_the_same_name_again_replaces_its_patterns()
    {
        using var repo = FixtureRepo.CreateEmpty(StandardFiles());
        var calc = repo.PathOf("Lib/Calc.cs");
        var report = repo.PathOf("App/Report.cs");
        var store = new SurfaceGuardStore(repo.Root);
        store.Apply(Freeze("contracts", "Lib/**"));
        store.Apply(Freeze("contracts", "App/**"));

        Assert.Empty(store.Violations([calc], Declarations(calc, "public static int Add(int a, int b)")));
        Assert.Single(store.Violations([report], Declarations(report, "public class Report")));
    }

    [Fact]
    public void Wildcard_segments_match_as_PathPattern_does()
    {
        using var repo = FixtureRepo.CreateEmpty(StandardFiles());
        var core = repo.PathOf("Lib/Core1/Api.cs");
        var store = new SurfaceGuardStore(repo.Root);
        store.Apply(Freeze("contracts", "Lib/Core?/*.cs"));

        var events = store.Violations([core], Declarations(core, "public class Api"));

        Assert.Single(events);
        Assert.Equal("Lib/Core?/*.cs", events[0].ViolatedPattern);
    }

    [Fact]
    public void A_corrupt_line_is_skipped()
    {
        using var repo = FixtureRepo.CreateEmpty(StandardFiles());
        var calc = repo.PathOf("Lib/Calc.cs");
        Directory.CreateDirectory(repo.Root.StateDirectory);
        File.WriteAllLines(Path.Combine(repo.Root.StateDirectory, "guards.tsv"),
        [
            "no tab here",
            "\tLib/**",
            "contracts\t",
            "contracts\tLib/**\textra",
            "contracts\tLib/../**",
            "contracts\tLib/**",
        ]);

        var store = new SurfaceGuardStore(repo.Root);
        var events = store.Violations([calc], Declarations(calc, "public static int Add(int a, int b)"));

        Assert.Single(events);
        Assert.Equal("Lib/**", events[0].ViolatedPattern);
    }

    [Fact]
    public void Apply_and_Violations_can_run_concurrently()
    {
        using var repo = FixtureRepo.CreateEmpty(StandardFiles());
        var calc = repo.PathOf("Lib/Calc.cs");
        var store = new SurfaceGuardStore(repo.Root);

        Parallel.Invoke(
            () =>
            {
                for (var i = 0; i < 20; i++)
                    store.Apply(Freeze("contracts", "Lib/**"));
            },
            () =>
            {
                for (var i = 0; i < 20; i++)
                    Assert.Empty(store.Violations([], Declarations(calc, "public static int Add(int a, int b)")));
            });

        var final = store.Violations([calc], Declarations(calc, "public static int Add(int a, int b)"));
        Assert.Single(final);
    }

    private static FreezeGuardRequest Freeze(string name, params string[] patterns) => new(name, false, patterns);

    private static FreezeGuardRequest Unfreeze(string name) => new(name, true);

    private static Dictionary<RepoPath, IReadOnlyList<string>> Declarations(RepoPath file, params string[] headers) =>
        new() { [file] = headers };

    private static Dictionary<string, string> StandardFiles() => new()
    {
        ["Lib/Calc.cs"] = "namespace Lib { public static class Calc { public static int Add(int a, int b) => a + b; } }",
        ["App/Report.cs"] = "namespace App { public class Report { } }",
    };
}
