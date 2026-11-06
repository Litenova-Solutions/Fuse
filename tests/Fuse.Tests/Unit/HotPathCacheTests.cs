using System.IO.Pipes;
using Fuse.Check;
using Fuse.Engine.Client;
using Fuse.Repo;
using Fuse.Tests.Fixtures;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Fuse.Tests.Unit;

/// <summary>
///     The cheap paths of a check and a round trip: a session record and a changed file are hashed once while
///     they read the same, a single writer's session views and a cleared view's summary references are shed,
///     and a pipe is enumerated once for the answers a round trip asks for in quick succession.
/// </summary>
public class HotPathCacheTests
{
    [Fact]
    public void The_records_hash_is_kept_until_the_record_changes_and_still_ends_with_a_registration()
    {
        var root = FixtureRepo.CheckoutRoot;
        var calc = root.PathOf("Lib/Calc.cs");
        var now = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
        var running = true;
        var edits = new SessionEdits(root, null, null, clock: () => now, processIsRunning: _ => running);

        var before = edits.RecordsHash();
        Assert.Equal(before, edits.RecordsHash());

        edits.Record("claude:a", [calc]);
        var recorded = edits.RecordsHash();
        Assert.NotEqual(before, recorded);

        edits.MarkStopped("claude:a/agent-1");
        var stopped = edits.RecordsHash();
        Assert.NotEqual(recorded, stopped);

        edits.BeginRegistration("w1", ["Lib/**"], pid: 4242);
        var registered = edits.RecordsHash();
        Assert.NotEqual(stopped, registered);

        // A registration that ends by its hours or by its process exiting changes the hash, kept or not.
        now += SessionEdits.RegistrationLifetime;
        Assert.Equal(stopped, edits.RecordsHash());

        edits.BeginRegistration("w2", ["Lib/**"], pid: 4243);
        var withProcess = edits.RecordsHash();
        Assert.NotEqual(stopped, withProcess);
        running = false;
        Assert.Equal(stopped, edits.RecordsHash());

        edits.BeginRegistration("w3", ["Lib/**"]);
        Assert.NotEqual(stopped, edits.RecordsHash());
        Assert.True(edits.EndRegistration("w3"));
        Assert.Equal(stopped, edits.RecordsHash());
    }

    [Fact]
    public void A_file_with_the_stamp_it_was_hashed_under_is_not_read_for_the_same_key()
    {
        InTempDirectory(directory =>
        {
            var file = Path.Combine(directory, "Calc.cs");
            File.WriteAllText(file, "class Calc {}\n");
            var keyed = new[] { ("Calc.cs", file) };

            var first = KeptCheck.KeyFor("all", null, true, "head", 1, 1, "records", keyed);
            var stamp = File.GetLastWriteTimeUtc(file);

            // The same length under the same stamp answers from the kept hash, though the bytes differ.
            File.WriteAllText(file, "class Xalc {}\n");
            File.SetLastWriteTimeUtc(file, stamp);
            Assert.Equal(first, KeptCheck.KeyFor("all", null, true, "head", 1, 1, "records", keyed));

            File.SetLastWriteTimeUtc(file, stamp.AddSeconds(1));
            Assert.NotEqual(first, KeptCheck.KeyFor("all", null, true, "head", 1, 1, "records", keyed));

            File.Delete(file);
            Assert.Null(KeptCheck.KeyFor("all", null, true, "head", 1, 1, "records", keyed));
        });
    }

    [Fact]
    public void The_file_hashes_are_bounded()
    {
        InTempDirectory(directory =>
        {
            for (var i = 0; i < KeptCheck.MaxFileHashEntries + 12; i++)
            {
                var file = Path.Combine(directory, $"C{i}.cs");
                File.WriteAllText(file, $"class C{i} {{}}\n");
                Assert.NotNull(KeptCheck.KeyFor("all", null, true, "head", 1, 1, "records", [($"C{i}.cs", file)]));
            }

            Assert.True(KeptCheck.CachedFileHashes <= KeptCheck.MaxFileHashEntries);
        });
    }

    [Fact]
    public async Task A_check_answered_to_the_only_writer_sheds_the_session_views()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        engine.Repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Hail(string name);");
        await engine.SyncAsync("Lib/Calc.cs", "Lib/Greeting.cs");
        engine.Sessions.Record("A", [engine.Repo.PathOf("Lib/Calc.cs")]);
        engine.Sessions.Record("B", [engine.Repo.PathOf("Lib/Greeting.cs")]);

        var wider = await engine.CheckAllAsSessionAsync("A");
        Assert.NotEmpty(wider.Errors);
        Assert.True(engine.Workspace.SessionViewCount > 0);

        // Greeting is as HEAD leaves it again, so A is the only session the changed files name.
        engine.Repo.Replace("Lib/Greeting.cs", "string Hail(string name);", "string Greet(string name);");
        await engine.SyncAsync("Lib/Calc.cs", "Lib/Greeting.cs");
        var own = await engine.CheckAsSessionAsync("A", "Lib/Calc.cs");

        Assert.Equal(0, engine.Workspace.SessionViewCount);
        Assert.Contains(own.Errors, e => e.Error.Message.Contains("'Calc' does not contain a definition for 'Add'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clearing_the_views_releases_the_summary_references_they_kept()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        using var tracker = new ChangeTracker(repo.Root);
        using var projects = new ProjectLoader(repo.Root, _ => { });
        var views = new SolutionViews(repo.Root, tracker, projects);
        var cache = views.Summaries;
        var project = ProjectWith(repo, "public class C { public int M() => 1; }\n");
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = await cache.GetOrEmitAsync(project, "hot-path-key", cancellationToken);
        Assert.NotNull(first);
        Assert.Same(first, cache.TryGet("hot-path-key", project));

        views.Clear();
        var second = cache.TryGet("hot-path-key", project);

        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Equal(
            Assert.IsType<PortableExecutableReference>(first, exactMatch: false).FilePath,
            Assert.IsType<PortableExecutableReference>(second, exactMatch: false).FilePath);

        cache.ReleaseReferences();
        Assert.NotSame(second, cache.TryGet("hot-path-key", project));
    }

    [Fact]
    public void Pipe_existence_is_kept_for_a_hundred_milliseconds_per_name()
    {
        var name = "fuse-hotpath-" + Guid.NewGuid().ToString("N");
        var now = Environment.TickCount64;

        Assert.False(EngineClient.PipeExists(name, now));
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        // The pipe is there, but the answer kept at now holds for its hundred milliseconds.
        Assert.False(EngineClient.PipeExists(name, now + 50));
        Assert.True(EngineClient.PipeExists(name, now + 101));
        Assert.True(EngineClient.PipeExists(name));
    }

    /// <summary>One project over <paramref name="source"/>, in an adhoc solution whose only file the repository holds.</summary>
    private static Project ProjectWith(FixtureRepo repo, string source)
    {
        var project = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "P", "P", LanguageNames.CSharp, filePath: repo.Full("P.csproj"))
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithParseOptions(new CSharpParseOptions())
            .WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution.AddProject(project);
        solution = solution.AddDocument(DocumentId.CreateNewId(project.Id), "C.cs", SourceText.From(source), filePath: repo.Full("C.cs"));
        return solution.Projects.Single();
    }

    private static void InTempDirectory(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "fuse-hotpath-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            test(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
