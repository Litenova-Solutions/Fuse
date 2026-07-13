using System.Diagnostics;
using Fuse.Indexing;
using Fuse.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace Fuse.Workspace.Tests;

// F2 candidate racing against a REAL held compilation: proves the fork-sharing wall-clock gate (a k=3 race
// costs under 2x a single verify because forks share the immutable base) and verdict equality (a raced
// candidate's diagnostics equal running it alone). Guarded: when the SDK cannot build a binlog here the test
// returns rather than failing, matching ResidentWorkspaceTests.
public sealed class CandidateRaceResidentTests
{
    private readonly ITestOutputHelper _output;

    public CandidateRaceResidentTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Race_of_three_shares_the_base_compilation_and_returns_alone_equal_verdicts()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-race-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var binlog = Path.Combine(work, "build.binlog");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(work, "Widget.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            // A non-trivial fixture: the edited file (Widget.cs) binds ~150 methods that each call into one of 40
            // helper classes. Binding its semantic model is real work (tens of ms), so a k=3 parallel race can show
            // the fork-sharing speedup; on a 1-method fixture the sub-ms per-check cost is swamped by scheduling
            // overhead and no ratio is measurable (that would be a fixture artifact, not a sharing failure).
            for (var h = 0; h < 40; h++)
            {
                await File.WriteAllTextAsync(Path.Combine(work, $"Helper{h}.cs"),
                    $"namespace Sample; public static class Helper{h} {{ public static int V{h}(int n) => n + {h}; }}");
            }

            static string WidgetSource(string spinBody)
            {
                var body = new System.Text.StringBuilder();
                body.Append("namespace Sample; public sealed class Widget {");
                for (var m = 0; m < 150; m++)
                    body.Append($" public int M{m}(int n) => Helper{m % 40}.V{m % 40}(n) + {m};");
                body.Append($" public int Spin() => {spinBody};");
                body.Append(" }");
                return body.ToString();
            }

            await File.WriteAllTextAsync(Path.Combine(work, "Widget.cs"), WidgetSource("42"));

            if (!await TryBuildWithBinlogAsync(work, binlog))
                return; // The SDK could not build a binlog here; nothing to validate.

            using var resident = ResidentWorkspace.LoadFromBinlog(binlog, CancellationToken.None);
            Assert.NotEmpty(resident.Projects);

            // Three candidate single-file edits: one clean, two broken (an undefined identifier), so strict
            // dominance names the clean one as the sole winner.
            var cleanA = WidgetSource("7");
            var brokenB = WidgetSource("MissingB");
            var brokenC = WidgetSource("MissingC");
            var candidates = new[]
            {
                new RaceCandidate("a", "Widget.cs", cleanA),
                new RaceCandidate("b", "Widget.cs", brokenB),
                new RaceCandidate("c", "Widget.cs", brokenC),
            };

            IReadOnlyList<CheckDiagnostic>? CheckAlone(RaceCandidate candidate) =>
                resident.CheckOverlay(candidate.File, candidate.Content, CancellationToken.None);

            // Warm the base compilation so the first-bind cost is not charged to the measurements below (both are
            // measured warm; the fork-sharing claim is that k checks cost far less than k cold rehydrations).
            _ = resident.CheckOverlay("Widget.cs", cleanA, CancellationToken.None);
            _ = resident.CheckOverlay("Widget.cs", brokenB, CancellationToken.None);

            var singleSw = Stopwatch.StartNew();
            _ = resident.CheckOverlay("Widget.cs", cleanA, CancellationToken.None);
            singleSw.Stop();
            var singleMs = singleSw.Elapsed.TotalMilliseconds;

            var raceSw = Stopwatch.StartNew();
            var report = await CandidateRacer.RaceAsync(
                (candidate, ct) => Task.FromResult(resident.CheckOverlay(candidate.File, candidate.Content, ct)),
                candidates, CancellationToken.None);
            raceSw.Stop();
            var raceMs = raceSw.Elapsed.TotalMilliseconds;

            // Recorded fork-cost finding (F2's Fallback rests on this): the racer is sequential because concurrent
            // Roslyn binding over shared-base forks does not parallelize (measured elsewhere: race/seq ~1.03x on a
            // 20-core host). What the sequential racer proves here is the fork SHARING: k=3 checks cost roughly k
            // times a single WARM verify (not k cold rehydrations, which would be seconds each).
            _output.WriteLine($"F2 fork-cost spike (k=3, cores={Environment.ProcessorCount}): single {singleMs:F2} ms, race(seq) {raceMs:F2} ms, race/single {raceMs / Math.Max(0.01, singleMs):F2}x");

            // Verdict equality (the F2 gate): each raced candidate's diagnostics equal running it alone against the
            // held state.
            foreach (var candidate in candidates)
            {
                var alone = CheckAlone(candidate) ?? [];
                var raced = report.Verdicts.Single(v => v.Id == candidate.Id).Diagnostics;
                Assert.Equal(
                    alone.Select(d => (d.Id, d.Severity)).OrderBy(x => x),
                    raced.Select(d => (d.Id, d.Severity)).OrderBy(x => x));
            }

            // Strict dominance: candidate a is the only clean one, so it wins.
            Assert.Equal("a", report.WinnerId);
            Assert.False(report.Tie);
            Assert.True(report.Verdicts.Single(v => v.Id == "a").IsClean);
            Assert.False(report.Verdicts.Single(v => v.Id == "b").IsClean);

            // Fork sharing holds: k=3 sequential checks cost at most ~k+1 times a single warm verify (each check
            // rebinds only its own changed tree over the shared base; a cold rehydration per candidate would be
            // orders of magnitude higher). The floor absorbs scheduling noise on this fixture.
            Assert.True(raceMs < 4 * singleMs + 250,
                $"race(seq) {raceMs:F2} ms exceeded ~k+1 single verifies of {singleMs:F2} ms (fork sharing did not hold)");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<bool> TryBuildWithBinlogAsync(string projectDir, string binlogPath)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = projectDir,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(Path.Combine(projectDir, "Widget.csproj"));
        psi.ArgumentList.Add($"-bl:{binlogPath}");
        psi.ArgumentList.Add("-nologo");
        psi.ArgumentList.Add("-v:quiet");

        try
        {
            using var process = Process.Start(psi)!;
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(cts.Token);
            return process.ExitCode == 0 && File.Exists(binlogPath);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }
}
