using System.Collections.Immutable;
using Fuse.Check.Analyzers;
using Fuse.Check.Diagnostics;
using Fuse.Engine.Routing;
using Fuse.Engine.Scheduling;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
using Fuse.Protocol.Transport;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
namespace Fuse.Tests.Engine;

/// <summary>
///     After HEAD moves, the engine warms the baseline compilation-wide analyzer results in the background, one project
///     at a time and between requests, so the first check that needs them finds them running or done.
/// </summary>
public class BaselineWarmerTests
{
    [Fact]
    public async Task A_commit_starts_the_background_baseline_run()
    {
        using var repo = CreateAnalyzerRepo();
        await using var engine = await UnlimitedEngine.StartAsync(repo);
        var part = repo.Full("App/Part0.cs");
        Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync(part));

        repo.Replace("App/Part0.cs", "var total = 0;", "var total = 1;");
        repo.Commit("body edit");
        var answer = Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync(part)).Report;
        Assert.Empty(answer.Errors);

        await WaitForLogAsync(engine, "baseline warm: App compilation-wide analyzers done in ");
    }

    [Fact]
    public async Task A_check_waits_for_the_warm_run_instead_of_starting_another()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var slow = new SlowDeclAnalyzer();
        var project = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "P", "P", LanguageNames.CSharp)
            .WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)])
            .WithAnalyzerReferences([new AnalyzerImageReference([slow])]);
        using var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution.AddProject(project);
        solution = solution.AddDocument(DocumentId.CreateNewId(project.Id), "C0.cs", "public class C0 { }\n", filePath: repo.Full("C0.cs"));
        solution = solution.AddDocument(DocumentId.CreateNewId(project.Id), "C1.cs", "public class Widget1 { }\n", filePath: repo.Full("C1.cs"));
        var collector = new DiagnosticCollector(repo.Root, () => 0, _ => { });
        var cancellationToken = TestContext.Current.CancellationToken;

        collector.Begin(AnalyzerRun.Full, reuse: false);
        Assert.True(await collector.WarmBaselineProjectAsync(solution.Projects.Single(), 0, cancellationToken));
        var afterWarm = slow.Starts;
        var errors = await collector.ForFileAsync(solution, repo.Root.PathOf(repo.Full("C1.cs")), cancellationToken);

        Assert.Equal(afterWarm, slow.Starts);
        Assert.Equal("DECL2", Assert.Single(errors).Id);
    }

    [Fact]
    public async Task A_request_is_admitted_between_two_warmed_projects()
    {
        using var repo = CreateAnalyzerRepo();
        await using var engine = await UnlimitedEngine.StartAsync(repo);
        Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync(repo.Full("App/Part0.cs")));
        await WaitForLogAsync(engine, "baseline warm: App ");

        repo.Replace("App2/Part0.cs", "var total = 0;", "var total = 1;");
        repo.Commit("move HEAD");
        Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync(repo.Full("App2/Part0.cs")));

        var check = await engine.SendAsync(new EngineRequest.CheckChanges(WaitForLoad: true));

        Assert.IsType<EngineResponse.CheckAnswered>(check);
        await WaitForLogAsync(engine, "baseline warm: App2 ");
    }

    [Fact]
    public async Task A_second_HEAD_move_discards_the_first_warm_run()
    {
        using var repo = CreateAnalyzerRepo(startMs: 3000);
        await using var engine = await UnlimitedEngine.StartAsync(repo);
        var part = repo.Full("App/Part0.cs");
        Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync(part));
        await WaitForLogAsync(engine, "baseline warm: App ");

        repo.Replace("App/Part0.cs", "var total = 0;", "var total = 1;");
        repo.Commit("first");
        Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync(part));

        repo.Replace("App/Part0.cs", "var total = 1;", "var total = 2;");
        repo.Commit("second");
        var clean = Assert.IsType<EngineResponse.CheckAnswered>(await engine.SendAsync(new EngineRequest.CheckChanges(WaitForLoad: true))).Report;
        Assert.Empty(clean.Errors);

        // Whether the warm run for the first move is still running when HEAD moves again depends on timing; either way
        // the next checks must answer from the newest HEAD.

        repo.Replace("App/Part0.cs", "var total = 2;", "var total = missing;");
        var broken = Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync(part)).Report;
        Assert.Equal("CS0103", Assert.Single(broken.Errors).Error.Id);
    }

    [Fact]
    public void Each_project_gets_a_thirty_second_analyzer_budget()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), DiagnosticCollector.WarmBudget);
    }

    [Fact]
    public async Task The_warm_stops_when_a_newer_generation_cancels_its_token()
    {
        using var repo = CreateAnalyzerRepo(startMs: 2000);
        await using var engine = await UnlimitedEngine.StartAsync(repo);
        var part = repo.Full("App/Part0.cs");
        Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync(part));
        await WaitForLogAsync(engine, "baseline warm: App compilation-wide analyzers done in ");

        repo.Replace("App/Part0.cs", "var total = 0;", "var total = 1;");
        repo.Commit("second");
        Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync(part));

        repo.Replace("App/Part0.cs", "var total = 1;", "var total = 2;");
        repo.Commit("third");
        Assert.IsType<EngineResponse.CheckAnswered>(await engine.SendAsync(new EngineRequest.CheckChanges(WaitForLoad: true)));

        // The newest HEAD cancels the warm of the one before it: at most the first warm and the newest one finish.
        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.InRange(CountOccurrences(engine.Log, "baseline warm: App "), 1, 2);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    /// <summary>
    ///     A repository with a slow compilation-wide analyzer and two small app projects using it, built. The
    ///     analyzer's start sleeps <paramref name="startMs"/>, past the compilation-wide threshold.
    /// </summary>
    private static FixtureRepo CreateAnalyzerRepo(int startMs = 1200)
    {
        var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            [".gitignore"] = "bin/\nobj/\n",
            ["Directory.Build.props"] = "<Project />\n",
            ["Directory.Build.targets"] = "<Project />\n",
            ["Slow/Slow.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>netstandard2.0</TargetFramework>
                    <LangVersion>latest</LangVersion>
                    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
                    <IsRoslynComponent>true</IsRoslynComponent>
                    <NoWarn>RS1030;RS2008</NoWarn>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.8.0" PrivateAssets="all" />
                  </ItemGroup>
                </Project>
                """,
            ["Slow/SlowAnalyzer.cs"] = $$"""
                using System.Collections.Immutable;
                using Microsoft.CodeAnalysis;
                using Microsoft.CodeAnalysis.Diagnostics;

                namespace Slow.Analyzers;

                [DiagnosticAnalyzer(LanguageNames.CSharp)]
                public sealed class SlowAnalyzer : DiagnosticAnalyzer
                {
                    private static readonly DiagnosticDescriptor Rule = new("SLOW001", "Forbidden type", "Type {0} is forbidden", "Design", DiagnosticSeverity.Error, isEnabledByDefault: true);

                    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

                    public override void Initialize(AnalysisContext context)
                    {
                        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
                        context.EnableConcurrentExecution();
                        context.RegisterCompilationStartAction(start =>
                        {
                            System.Threading.Thread.Sleep({{startMs}});
                            start.RegisterSymbolAction(
                                symbol =>
                                {
                                    if (symbol.Symbol.Name.StartsWith("Forbidden", System.StringComparison.Ordinal))
                                        symbol.ReportDiagnostic(Diagnostic.Create(Rule, symbol.Symbol.Locations[0], symbol.Symbol.Name));
                                },
                                SymbolKind.NamedType);
                        });
                    }
                }
                """,
            ["App/App.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Slow/Slow.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
                  </ItemGroup>
                </Project>
                """,
            ["App/Part0.cs"] = "namespace App;\n\npublic static class Part0\n{\n    public static int Sum(int n)\n    {\n        var total = 0;\n        for (var k = 0; k < n; k++)\n            total += k;\n        return total;\n    }\n}\n",
            ["App2/App2.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Slow/Slow.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
                  </ItemGroup>
                </Project>
                """,
            ["App2/Part0.cs"] = "namespace App2;\n\npublic static class Part0\n{\n    public static int Sum(int n)\n    {\n        var total = 0;\n        for (var k = 0; k < n; k++)\n            total += k;\n        return total;\n    }\n}\n",
        });
        FixtureRepo.Run(repo.Path, "dotnet", "build", "App/App.csproj", "-nologo", "-v:q");
        FixtureRepo.Run(repo.Path, "dotnet", "build", "App2/App2.csproj", "-nologo", "-v:q");
        return repo;
    }

    /// <summary>Waits up to a minute for the router's log to hold <paramref name="text"/>, which a background warm writes when it gets there.</summary>
    private static async Task WaitForLogAsync(UnlimitedEngine engine, string text)
    {
        for (var i = 0; i < 600 && !engine.Log.Contains(text, StringComparison.Ordinal); i++)
            await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.True(engine.Log.Contains(text, StringComparison.Ordinal), $"the log never held \"{text}\":\n{engine.Log}");
    }

    /// <summary>
    ///     The engine's request loop with a memory budget that never sheds, so a background warm runs even when
    ///     the machine that runs the tests is short on memory. The production budget still sheds background work
    ///     past its ceiling; this one only keeps the tests from depending on how much memory their machine holds.
    /// </summary>
    private sealed class UnlimitedEngine : IAsyncDisposable
    {
        private readonly RequestRouter _router;
        private readonly EngineLog _log;
        private readonly CancellationTokenSource _shutdown;

        private UnlimitedEngine(RequestRouter router, EngineLog log, CancellationTokenSource shutdown)
        {
            _router = router;
            _log = log;
            _shutdown = shutdown;
        }

        public static async Task<UnlimitedEngine> StartAsync(FixtureRepo repo)
        {
            var log = new EngineLog(repo.Root.StateDirectory);
            var scheduler = new Scheduler(Scheduler.DefaultReaderCount);
            var budget = new MemoryBudget(ceilingBytes: 1L << 60, workingSet: () => 0, memoryLoad: () => 0, totalAvailable: () => 1L << 60);
            var router = new RequestRouter(repo.Root, log, scheduler, budget);
            var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            await router.InitializeAsync(shutdown.Token).ConfigureAwait(false);
            return new UnlimitedEngine(router, log, shutdown);
        }

        public Task<EngineResponse> SendAsync(EngineRequest request, CancellationToken? cancellationToken = null) =>
            _router.HandleAsync(request with { BuildId = EngineVersion.Build }, cancellationToken ?? TestContext.Current.CancellationToken);

        public Task<EngineResponse> CheckAsync(params string[] files) =>
            SendAsync(files.Length == 0 ? new EngineRequest.CheckChanges(WaitForLoad: true) : new EngineRequest.CheckFiles(files, WaitForLoad: true));

        public string Log
        {
            get
            {
                if (!File.Exists(_log.FilePath))
                    return "";
                using var stream = new FileStream(_log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
            await _router.WaitForPreloadAsync().ConfigureAwait(false);
            _router.Dispose();
            _shutdown.Dispose();
        }
    }

    /// <summary>Reports every class whose name starts with Widget, after a compilation start past the compilation-wide threshold.</summary>
#pragma warning disable RS1001, RS1025, RS1026, RS2008
    private sealed class SlowDeclAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new("DECL2", "Declaration", "class {0}", "Test", DiagnosticSeverity.Error, isEnabledByDefault: true);
        private int _starts;

        public int Starts => Volatile.Read(ref _starts);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                Interlocked.Increment(ref _starts);
                Thread.Sleep(AnalyzerPlan.CompilationWideStart + TimeSpan.FromMilliseconds(100));
                start.RegisterSymbolAction(
                    s =>
                    {
                        if (s.Symbol.Name.StartsWith("Widget", StringComparison.Ordinal))
                            s.ReportDiagnostic(Diagnostic.Create(Rule, s.Symbol.Locations[0], s.Symbol.Name));
                    },
                    SymbolKind.NamedType);
            });
        }
    }
#pragma warning restore RS1001, RS1025, RS1026, RS2008
}
