using System.Diagnostics;
using Fuse.Check;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Fuse.Tests.Engine;

/// <summary>
///     Verifies <see cref="SyntaxTokenIndex"/>, <see cref="SpanChecker"/>, <see cref="DeclarationFilter"/> caching,
///     parallel check pipelines, and implicit/operator fallbacks in <see cref="ChangeReach"/>.
/// </summary>
public class SyntaxIndexAndSpeedTests
{
    [Fact]
    public async Task SyntaxTokenIndex_narrows_candidates_across_identifiers_inheritance_partials_attributes_and_aliases()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["Aliases.cs"] = "global using CalcAlias = App.TargetType;\n",
            ["Base.cs"] = "namespace App;\npublic interface IBase { void Ping(); }\npublic class TargetType { }\n",
            ["Mid.cs"] = "namespace App;\npublic partial class Mid : IBase { public void Ping() { } }\n",
            ["Mid.Extra.cs"] = "namespace App;\npublic partial class Mid { public void Extra() { } }\n",
            ["Leaf.cs"] = "namespace App;\npublic sealed class Leaf : Mid { }\n",
            ["AliasDerived.cs"] = "namespace App;\npublic sealed class AliasDerived : CalcAlias { }\n",
            ["CustomTagAttribute.cs"] = "namespace App;\n[System.AttributeUsage(System.AttributeTargets.Class)]\npublic sealed class CustomTagAttribute : System.Attribute { }\n",
            ["Tagged.cs"] = "namespace App;\n[CustomTag]\npublic sealed class Tagged { }\n",
            ["Unrelated.cs"] = "namespace App;\npublic sealed class Unrelated { public int Value() => 42; }\n",
        });

        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo.Create(projectId, VersionStamp.Create(), "TestProj", "TestProj", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        using var ws = new AdhocWorkspace();
        var solution = ws.CurrentSolution.AddProject(projectInfo);
        foreach (var file in Directory.EnumerateFiles(repo.Root.Path, "*.cs"))
        {
            var docId = DocumentId.CreateNewId(projectId);
            solution = solution.AddDocument(docId, Path.GetFileName(file), File.ReadAllText(file), filePath: file);
        }

        var index = new SyntaxTokenIndex(repo.Root);
        var ct = TestContext.Current.CancellationToken;
        await index.EnsureIndexedAsync(solution, ct);

        Assert.Equal(9, index.DocumentCount);

        var pingDocs = index.CandidateDocuments(solution, ["Ping"], []);
        var pingFiles = pingDocs.Select(d => Path.GetFileName(d.FilePath)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["Base.cs", "Mid.cs"], pingFiles);

        var inheritedDocs = index.CandidateDocuments(solution, [], ["IBase"]);
        var inheritedFiles = inheritedDocs.Select(d => Path.GetFileName(d.FilePath)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["Leaf.cs", "Mid.Extra.cs", "Mid.cs"], inheritedFiles);

        var attrDocs = index.CandidateDocuments(solution, ["CustomTagAttribute"], []);
        var attrFiles = attrDocs.Select(d => Path.GetFileName(d.FilePath)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["CustomTagAttribute.cs", "Tagged.cs"], attrFiles);

        var aliasDerivedDocs = index.CandidateDocuments(solution, [], ["TargetType"]);
        Assert.Contains(aliasDerivedDocs, d => Path.GetFileName(d.FilePath) == "AliasDerived.cs");
    }

    [Fact]
    public async Task SyntaxTokenIndex_persists_validates_stamps_and_updates_incrementally()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["A.cs"] = "public class A { public void Alpha() { } }\n",
            ["B.cs"] = "public class B { public void Beta() { } }\n",
        });

        using var ws = new AdhocWorkspace();
        var pid = ProjectId.CreateNewId();
        var solution = ws.CurrentSolution.AddProject(ProjectInfo.Create(pid, VersionStamp.Create(), "P", "P", LanguageNames.CSharp));
        var docA = DocumentId.CreateNewId(pid);
        var docB = DocumentId.CreateNewId(pid);
        solution = solution
            .AddDocument(docA, "A.cs", repo.Read("A.cs"), filePath: repo.Full("A.cs"))
            .AddDocument(docB, "B.cs", repo.Read("B.cs"), filePath: repo.Full("B.cs"));

        var ct = TestContext.Current.CancellationToken;
        var index = new SyntaxTokenIndex(repo.Root);
        await index.EnsureIndexedAsync(solution, ct);
        index.Save("test-key");

        var expectedFile = Path.Combine(repo.Root.SharedStateDirectory, "token-index", "test-key.bin");
        Assert.True(File.Exists(expectedFile));

        // Modify B.cs on disk with newer timestamp and length before reload.
        await Task.Delay(50, ct);
        repo.Write("B.cs", "public class B { public void Gamma() { } }\n// extra text so length differs\n");

        var reloaded = new SyntaxTokenIndex(repo.Root);
        Assert.True(reloaded.TryLoad("test-key"));
        // A.cs stamp matched and stayed loaded; B.cs stamp changed so it was invalidated.
        Assert.Equal(1, reloaded.DocumentCount);
        Assert.Equal(["A.cs"], reloaded.CandidatePaths(["Alpha"], []));
        Assert.Empty(reloaded.CandidatePaths(["Beta"], []));

        // Incremental single-document update replaces tokens cleanly.
        var updatedRoot = CSharpSyntaxTree.ParseText(repo.Read("B.cs"), cancellationToken: ct).GetRoot(ct);
        var sw = Stopwatch.StartNew();
        reloaded.UpdateDocument(repo.Full("B.cs"), updatedRoot);
        sw.Stop();
        Assert.Equal(["B.cs"], reloaded.CandidatePaths(["Gamma"], []));
        Assert.Empty(reloaded.CandidatePaths(["Beta"], []));
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task SyntaxTokenIndex_preserves_tokens_across_multi_targeted_preprocessor_branches()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["Shape.cs"] = "#if NET10_0_OR_GREATER\npublic class Shape { public void Net10Only() { } }\n#endif\n",
        });

        using var ws = new AdhocWorkspace();
        var p8 = ProjectId.CreateNewId();
        var p10 = ProjectId.CreateNewId();
        var opts8 = CSharpParseOptions.Default;
        var opts10 = CSharpParseOptions.Default.WithPreprocessorSymbols("NET10_0_OR_GREATER");

        var solution = ws.CurrentSolution
            .AddProject(ProjectInfo.Create(p8, VersionStamp.Create(), "P.net8", "P", LanguageNames.CSharp, parseOptions: opts8))
            .AddProject(ProjectInfo.Create(p10, VersionStamp.Create(), "P.net10", "P", LanguageNames.CSharp, parseOptions: opts10))
            .AddDocument(DocumentId.CreateNewId(p8), "Shape.cs", repo.Read("Shape.cs"), filePath: repo.Full("Shape.cs"))
            .AddDocument(DocumentId.CreateNewId(p10), "Shape.cs", repo.Read("Shape.cs"), filePath: repo.Full("Shape.cs"));

        var index = new SyntaxTokenIndex(repo.Root);
        await index.EnsureIndexedAsync(solution, TestContext.Current.CancellationToken);

        var matches = index.CandidateDocuments(solution, ["Net10Only"], []);
        Assert.NotEmpty(matches);
    }

    [Fact]
    public async Task ChangeReach_with_token_index_handles_implicit_operators_constructors_indexers_and_attributes()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Write("Lib/Fancy.cs", """
            namespace Lib;

            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class FancyTagAttribute : System.Attribute
            {
                public FancyTagAttribute(int level) => Level = level;
                public int Level { get; }
            }

            public class Meter
            {
                public int this[int index] => index;
                public static Meter operator +(Meter a, Meter b) => a;
                public static implicit operator int(Meter m) => 1;
            }
            """);
        repo.Write("App/UseFancy.cs", """
            namespace App;

            [Lib.FancyTag(1)]
            public class UseFancy
            {
                public static int Run()
                {
                    var m = new Lib.Meter();
                    var added = m + m;
                    int n = added;
                    return added[0] + n;
                }
            }
            """);
        repo.Commit("add implicit constructs");

        await using var engine = await InProcessEngine.StartAsync(repo);

        // 1. Attribute constructor/class change when usage omits Attribute suffix ([FancyTag(1)]).
        repo.Replace("Lib/Fancy.cs", "public FancyTagAttribute(int level)", "public FancyTagAttribute(string level)");
        var attrResult = await engine.CheckAsync("Lib/Fancy.cs");
        Assert.Contains(attrResult.Errors, e => e.Error.Path == "App/UseFancy.cs" && e.Error.Id == "CS1503");

        // Reset and test operator + and implicit conversion + indexer.
        repo.Replace("Lib/Fancy.cs", "public FancyTagAttribute(string level)", "public FancyTagAttribute(int level)");
        repo.Replace("Lib/Fancy.cs", "public int this[int index]", "public int this[string index]");
        var idxResult = await engine.CheckAsync("Lib/Fancy.cs");
        Assert.Contains(idxResult.Errors, e => e.Error.Path == "App/UseFancy.cs" && e.Error.Id == "CS1503");
    }

    [Fact]
    public void SpanChecker_detects_body_only_edits_and_falls_back_outside_bodies()
    {
        var baseSource = """
            namespace Demo;

            public class Sample
            {
                private int _field = 1;
                public int Prop => _field;
                public int Value { get => _field; set => _field = value; }

                public int Compute(int x)
                {
                    return x + 1;
                }
            }
            """;

        var bodyEdit = baseSource.Replace("return x + 1;", "return x + 2;", StringComparison.Ordinal);
        var accessorEdit = baseSource.Replace("set => _field = value;", "set => _field = value * 2;", StringComparison.Ordinal);
        var usingEdit = "using System;\n" + baseSource;
        var signatureEdit = baseSource.Replace("public int Compute(int x)", "public long Compute(int x)", StringComparison.Ordinal);
        var fieldInitEdit = baseSource.Replace("private int _field = 1;", "private int _field = 2;", StringComparison.Ordinal);
        var directiveEdit = "#pragma warning disable CS0219\n" + bodyEdit;
        var syntaxErrorEdit = baseSource.Replace("return x + 1;", "return x + ;", StringComparison.Ordinal);

        var ct = TestContext.Current.CancellationToken;
        var beforeTree = CSharpSyntaxTree.ParseText(baseSource, cancellationToken: ct);
        Assert.NotNull(SpanChecker.TryGetBodyOnlySpans(beforeTree.GetRoot(ct), CSharpSyntaxTree.ParseText(bodyEdit, cancellationToken: ct).GetRoot(ct)));
        Assert.NotNull(SpanChecker.TryGetBodyOnlySpans(beforeTree.GetRoot(ct), CSharpSyntaxTree.ParseText(accessorEdit, cancellationToken: ct).GetRoot(ct)));
        Assert.Null(SpanChecker.TryGetBodyOnlySpans(beforeTree.GetRoot(ct), CSharpSyntaxTree.ParseText(usingEdit, cancellationToken: ct).GetRoot(ct)));
        Assert.Null(SpanChecker.TryGetBodyOnlySpans(beforeTree.GetRoot(ct), CSharpSyntaxTree.ParseText(signatureEdit, cancellationToken: ct).GetRoot(ct)));
        Assert.Null(SpanChecker.TryGetBodyOnlySpans(beforeTree.GetRoot(ct), CSharpSyntaxTree.ParseText(fieldInitEdit, cancellationToken: ct).GetRoot(ct)));
        Assert.Null(SpanChecker.TryGetBodyOnlySpans(beforeTree.GetRoot(ct), CSharpSyntaxTree.ParseText(directiveEdit, cancellationToken: ct).GetRoot(ct)));
        Assert.Null(SpanChecker.TryGetBodyOnlySpans(beforeTree.GetRoot(ct), CSharpSyntaxTree.ParseText(syntaxErrorEdit, cancellationToken: ct).GetRoot(ct)));
    }

    [Fact]
    public async Task Checker_InteractiveFast_reports_body_errors_and_falls_back_on_declaration_changes()
    {
        await using var engine = await InProcessEngine.StartAsync();
        var fast = new CheckOptions(InteractiveFast: true);
        var ct = TestContext.Current.CancellationToken;

        // Body-only error in Strict/Thing.cs (including TreatWarningsAsErrors CS0219).
        engine.Repo.Replace("Strict/Thing.cs", "public static int Value() => 1;", "public static int Value()\n    {\n        int unused = 0;\n        return missingVar;\n    }");
        var fastBody = await engine.Checker.CheckAsync(
            engine.Files("Strict/Thing.cs"),
            null,
            new AnalyzerRun(CompilationWide: false, CancellationToken.None),
            PhaseTimes.None,
            fast,
            ct);

        Assert.Contains(fastBody.Errors, e => e.Error.Id == "CS0103" && e.Error.Path == "Strict/Thing.cs");
        Assert.Contains(fastBody.Errors, e => e.Error.Id == "CS0219" && e.Error.Path == "Strict/Thing.cs");
        Assert.Equal(0, fastBody.DependentProjectsChecked);

        // Declaration/signature edit automatically falls back to full-file + ChangeReach.
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        var fastDecl = await engine.Checker.CheckAsync(
            engine.Files("Lib/Calc.cs"),
            null,
            new AnalyzerRun(CompilationWide: false, CancellationToken.None),
            PhaseTimes.None,
            fast,
            ct);

        Assert.Equal(["App/Program.cs", "Lib.Tests/CalcTests.cs"], fastDecl.Errors.Select(e => e.Error.Path).Order(StringComparer.Ordinal));
        Assert.True(fastDecl.DependentProjectsChecked >= 2);
    }

    [Fact]
    public async Task DeclarationFilter_caches_head_declarations_across_multiple_errors()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "private int Add(");
        var result = await engine.CheckAsync("Lib/Calc.cs");
        engine.Sessions.Record("B", [engine.Repo.PathOf("Lib/Calc.cs")]);

        var errors = result.Errors.Select(e => e.Error).ToList();
        Assert.True(errors.Count >= 2);

        var generation = engine.Workspace.PinGeneration();
        var writers = engine.Sessions.WritersOf(generation.Changed);
        var filter = new DeclarationFilter(engine.Workspace, engine.Sessions);
        var ct = TestContext.Current.CancellationToken;

        foreach (var error in errors)
        {
            Assert.Equal(DeclarationFilter.Decision.NotTold, await filter.DecideAsync(error, "A", generation, writers, ct));
            Assert.Equal(DeclarationFilter.Decision.Told, await filter.DecideAsync(error, "B", generation, writers, ct));
        }

        // Baseline document or single HEAD parse serves every error without re-parsing HEAD per error.
        Assert.True(filter.HeadParses <= 1);
    }

    [Fact]
    public async Task Parallel_whole_project_binding_and_session_attribution_stay_deterministic()
    {
        var repo = FixtureRepo.CreateStandard();
        for (var i = 0; i < 510; i++)
            repo.Write($"Strict/Generated/Part{i}.cs", $"namespace Strict;\n\ninternal static class Part{i}\n{{\n    public static int Id() => {i};\n}}\n");
        repo.Commit("many strict files");

        await using var engine = await InProcessEngine.StartAsync(repo);
        engine.Repo.Replace("Strict/Thing.cs", "public static class Thing", "public static partial class Thing");
        engine.Repo.Replace("Strict/Thing.cs", "public static int Value() => 1;", "public static int Value() => new int[0].Length + 1;");
        var result = await engine.CheckAsync("Strict/Thing.cs");

        Assert.True(result.CheckedWholeProjects);
        Assert.Equal(511, result.FilesChecked);
        Assert.Equal("CA1825", Assert.Single(result.Errors).Error.Id);
    }
}
