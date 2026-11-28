using Fuse.Check.Analyzers;
using Fuse.Check.Enrichment;
using Fuse.Check.Model;
using Fuse.Check.Pipeline;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Check;
using Fuse.Protocol.Transport;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
namespace Fuse.Tests.Engine;

/// <summary>
///     Verifies <see cref="RepairPacket"/> AOT serialization and deterministic Roslyn repair hints built by
///     <see cref="RepairPackets"/>.
/// </summary>
public class RepairPacketTests
{
    [Fact]
    public void RepairPacket_round_trips_through_ProtocolJson_and_omits_when_null()
    {
        var packet = new RepairPacket(
            ["  1: var c = new Calc();", "> 2: var x = c.Ad(1, 2);", "  3: Console.WriteLine(x);"],
            ["int Calc.Add(int a, int b)"],
            ["using Lib;"],
            null);

        var withRepair = new CompilerError("App/Program.cs", 2, 15, "CS1061", "'Calc' does not contain a definition for 'Ad'")
        {
            Repair = packet,
        };
        var withoutRepair = new CompilerError("App/Program.cs", 2, 15, "CS1061", "'Calc' does not contain a definition for 'Ad'");

        var response = new EngineResponse.CheckAnswered(new CheckReport(
            [new ReportedError(withRepair), new ReportedError(withoutRepair)],
            FilesChecked: 1,
            Projects: ["App"],
            DeclarationsChangedIn: [],
            DependentProjectsChecked: 0,
            CheckedWholeProjects: false,
            ErrorCount: 2,
            ErrorFileCount: 1,
            LeftToOtherSessions: 0));

        var json = ProtocolJson.Serialize(response);
        Assert.Contains("\"repair\":", json, StringComparison.Ordinal);

        var roundTripped = Assert.IsType<EngineResponse.CheckAnswered>(ProtocolJson.ReadResponse(json));
        Assert.Equal(packet, roundTripped.Report.Errors[0].Error.Repair);
        Assert.Null(roundTripped.Report.Errors[1].Error.Repair);

        var cleanJson = ProtocolJson.Serialize(new EngineResponse.CheckAnswered(new CheckReport(
            [new ReportedError(withoutRepair)], 1, ["App"], [], 0, false, 1, 1, 0)));
        Assert.DoesNotContain("\"repair\"", cleanJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checker_attaches_CS1061_CS1503_CS7036_CS0246_and_CS0535_repair_packets()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Write("Lib/Extra.cs", """
            namespace Lib.Special;

            public static class SpecialExtensions
            {
                public static int Quad(this Lib.Calc calc, int value) => value * 4;
            }

            public sealed class ExtraType
            {
            }
            """);
        repo.Commit("add extra types");

        await using var engine = await InProcessEngine.StartAsync(repo);
        var ct = TestContext.Current.CancellationToken;
        var brief = new CheckOptions(InteractiveFast: false, IncludeRepairBrief: true);

        // 1. CS1061 missing instance member + extension method + 5-line source window.
        engine.Repo.Replace("App/Program.cs", "calc.Add(1, 2)", "calc.Ad(1, 2)");
        var cs1061Result = await engine.Checker.CheckAsync(engine.Files("App/Program.cs"), null, AnalyzerRun.Full, PhaseTimes.None, brief, ct);
        var cs1061Error = Assert.Single(cs1061Result.Errors);
        Assert.Equal("CS1061", cs1061Error.Error.Id);
        Assert.NotNull(cs1061Error.Repair);
        Assert.Equal(cs1061Error.Repair, cs1061Error.Error.Repair);
        Assert.Contains(cs1061Error.Repair.SourceWindow, l => l.StartsWith("> 3:", StringComparison.Ordinal) && l.Contains("calc.Ad", StringComparison.Ordinal));
        Assert.Equal("int Lib.Calc.Add(int a, int b)", cs1061Error.Repair.Candidates[0]);

        // 2. CS1503 argument type mismatch + candidate overloads.
        engine.Repo.Replace("App/Program.cs", "calc.Ad(1, 2)", "calc.Add(\"bad\", 2)");
        var cs1503Result = await engine.Checker.CheckAsync(engine.Files("App/Program.cs"), null, AnalyzerRun.Full, PhaseTimes.None, brief, ct);
        var cs1503Error = Assert.Single(cs1503Result.Errors);
        Assert.Equal("CS1503", cs1503Error.Error.Id);
        Assert.NotNull(cs1503Error.Repair);
        Assert.Contains(cs1503Error.Repair.Candidates, c => c.Contains("Add(int a, int b)", StringComparison.Ordinal));

        // 3. CS7036 missing required argument.
        engine.Repo.Replace("App/Program.cs", "calc.Add(\"bad\", 2)", "calc.Add(1)");
        var cs7036Result = await engine.Checker.CheckAsync(engine.Files("App/Program.cs"), null, AnalyzerRun.Full, PhaseTimes.None, brief, ct);
        var cs7036Error = Assert.Single(cs7036Result.Errors);
        Assert.Equal("CS7036", cs7036Error.Error.Id);
        Assert.NotNull(cs7036Error.Repair);
        Assert.Contains(cs7036Error.Repair.Candidates, c => c.Contains("Add(int a, int b)", StringComparison.Ordinal));

        // 4. CS0246 missing type -> suggested using directive.
        engine.Repo.Replace("App/Program.cs", "calc.Add(1)", "calc.Add(1));\nExtraType? unused = null; _ = (unused");
        var cs0246Result = await engine.Checker.CheckAsync(engine.Files("App/Program.cs"), null, AnalyzerRun.Full, PhaseTimes.None, brief, ct);
        var cs0246Error = Assert.Single(cs0246Result.Errors, e => e.Error.Id == "CS0246");
        Assert.NotNull(cs0246Error.Repair);
        Assert.Contains("using Lib.Special;", cs0246Error.Repair.SuggestedUsings);

        // Restore App/Program.cs and check CS0535 unimplemented interface stub.
        engine.Repo.Replace("App/Program.cs", "calc.Add(1));\nExtraType? unused = null; _ = (unused", "calc.Add(1, 2)");
        engine.Repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Greet(string name, bool loud);");
        var cs0535Result = await engine.Checker.CheckAsync(engine.Files("Lib/Greeting.cs"), null, AnalyzerRun.Full, PhaseTimes.None, brief, ct);
        var cs0535Error = Assert.Single(cs0535Result.Errors, e => e.Error.Id == "CS0535");
        Assert.NotNull(cs0535Error.Repair);
        Assert.NotNull(cs0535Error.Repair.InterfaceStub);
        Assert.Contains("public string Greet(string name, bool loud)", cs0535Error.Repair.InterfaceStub, StringComparison.Ordinal);
        Assert.Contains("throw new NotImplementedException()", cs0535Error.Repair.InterfaceStub, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepairPacketBuilder_builds_CS0117_static_members_and_extension_methods()
    {
        var code = """
            using System;

            namespace Demo;

            public static class Ext
            {
                public static int Triple(this Box box) => box.Value * 3;
            }

            public sealed class Box
            {
                public int Value => 7;
                public static Box CreateDefault() => new();
            }

            public static class Runner
            {
                public static int Test()
                {
                    _ = Box.CreateDefalt();
                    var b = new Box();
                    return b.Tripl();
                }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(code, cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create(
            "DemoAsm",
            [tree],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var ct = TestContext.Current.CancellationToken;
        var model = compilation.GetSemanticModel(tree);
        var text = await tree.GetTextAsync(ct);
        var diagnostics = compilation.GetDiagnostics(ct);

        var cs0117 = Assert.Single(diagnostics, d => d.Id == "CS0117");
        var staticPacket = RepairPackets.Build(cs0117, model, compilation, text, cancellationToken: ct);
        Assert.Equal("Box Box.CreateDefault()", staticPacket.Candidates[0]);

        var cs1061 = Assert.Single(diagnostics, d => d.Id == "CS1061");
        var extPacket = RepairPackets.Build(cs1061, model, compilation, text, cancellationToken: ct);
        Assert.Contains(extPacket.Candidates, c => c.Contains("Triple()", StringComparison.Ordinal) && c.Contains("extension", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("tenth")]
    [InlineData("eighth")]
    public async Task Brief_names_candidates_for_an_error_inside_one_framework_branch(string branch)
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            [".gitignore"] = "bin/\nobj/\n",
            ["Fixture.slnx"] = "<Solution>\n  <Project Path=\"Lib/Lib.csproj\" />\n</Solution>\n",
            ["Lib/Lib.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>net10.0;net8.0</TargetFrameworks>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """,
            ["Lib/Marker.cs"] = """
                namespace Lib;

                public static class Marker
                {
                #if NET10_0_OR_GREATER
                    public static string Which() => "tenth".ToString();
                #else
                    public static string Which() => "eighth".ToString();
                #endif
                }
                """,
        });
        FixtureRepo.Run(repo.Root.Path, "dotnet", "restore", "Fixture.slnx", "-nologo", "-v:q");
        await using var engine = await InProcessEngine.StartAsync(repo);
        engine.Repo.Replace("Lib/Marker.cs", $"\"{branch}\".ToString()", $"\"{branch}\".ToStrin()");
        await engine.SyncAsync("Lib/Marker.cs");

        var report = await engine.Checker.CheckAsync(
            new CheckScope.AllChanges(),
            null,
            AnalyzerRun.Full,
            PhaseTimes.None,
            new CheckOptions(IncludeRepairBrief: true),
            TestContext.Current.CancellationToken);

        var error = Assert.Single(report.Errors);
        Assert.Equal("CS1061", error.Error.Id);
        Assert.Contains(error.Repair!.Candidates, c => c.Contains("ToString", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Default_options_leave_repair_null_and_do_not_collide_with_brief_cache()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("App/Program.cs", "calc.Add(1, 2)", "calc.Ad(1, 2)");
        await engine.SyncAsync("App/Program.cs");

        var ct = TestContext.Current.CancellationToken;
        var plain = await engine.Checker.CheckAsync(new CheckScope.AllChanges(), null, AnalyzerRun.Full, PhaseTimes.None, CheckOptions.Default, ct);
        Assert.Null(Assert.Single(plain.Errors).Repair);

        var withBrief = await engine.Checker.CheckAsync(
            new CheckScope.AllChanges(),
            null,
            AnalyzerRun.Full,
            PhaseTimes.None,
            new CheckOptions(IncludeRepairBrief: true),
            ct);
        Assert.NotNull(Assert.Single(withBrief.Errors).Repair);
    }
}
