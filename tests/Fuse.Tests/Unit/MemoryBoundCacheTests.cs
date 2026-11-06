using System.Text;
using Fuse.Testing;
using Fuse.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Fuse.Tests.Unit;

/// <summary>
///     The emit cache keeps a limited number of images: past the limit the image unused the longest goes, and asking
///     for it again emits it again, while an image still kept is taken from the cache.
/// </summary>
public class MemoryBoundCacheTests
{
    [Fact]
    public async Task The_emit_cache_keeps_at_most_its_limit_and_emits_an_evicted_image_again()
    {
        var directory = FixtureRepo.NewDirectory();
        try
        {
            using var workspace = new AdhocWorkspace();
            var solution = workspace.CurrentSolution;
            var cache = new EmitCache();
            var projects = new List<Project>();
            for (var i = 0; i < EmitCache.MaxEntries + 4; i++)
            {
                // The build output of one project, as a build writes it: an assembly a build produced, not the
                // runtime's own core library, whose image no project's build output ever is.
                var output = Path.Combine(directory, $"P{i}.dll");
                File.Copy(typeof(MemoryBoundCacheTests).Assembly.Location, output);
                var source = Path.Combine(directory, $"C{i}.cs");
                File.WriteAllText(source, "public class C { }");
                // A library, as a project's build output is: a project the workspace makes without compilation
                // options is a program with no entry point, which does not compile and emits nothing.
                var info = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "P", "P", LanguageNames.CSharp, outputFilePath: output)
                    .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                    .WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
                solution = solution.AddProject(info)
                    .AddDocument(DocumentId.CreateNewId(info.Id), "C.cs", SourceText.From("public class C { }", Encoding.UTF8), filePath: source);
                var project = solution.GetProject(info.Id)!;
                projects.Add(project);
                Assert.NotNull(await cache.GetAsync(project, TestContext.Current.CancellationToken));
            }

            Assert.Equal(EmitCache.MaxEntries, cache.Entries);
            Assert.Equal(projects.Count, cache.Emits);
            Assert.NotNull(await cache.GetAsync(projects[^1], TestContext.Current.CancellationToken));
            Assert.Equal(projects.Count, cache.Emits);
            Assert.NotNull(await cache.GetAsync(projects[0], TestContext.Current.CancellationToken));
            Assert.Equal(projects.Count + 1, cache.Emits);
            Assert.Equal(EmitCache.MaxEntries, cache.Entries);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
