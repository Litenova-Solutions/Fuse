using Fuse.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Fuse.Tests.Unit;

public class BuiltResourcesTests
{
    [Fact]
    public void Embedded_resources_are_read_back_with_name_visibility_and_data()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fuse-resources-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        try
        {
            var assembly = Path.Combine(directory, "WithResources.dll");
            var compilation = CSharpCompilation.Create(
                "WithResources",
                [CSharpSyntaxTree.ParseText("public class C {}", cancellationToken: TestContext.Current.CancellationToken)],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var publicData = "zone data"u8.ToArray();
            var privateData = new byte[] { 1, 2, 3 };
            using (var output = File.Create(assembly))
            {
                var result = compilation.Emit(
                    output,
                    manifestResources:
                    [
                        new ResourceDescription("Data.Zones.nzd", () => new MemoryStream(publicData), isPublic: true),
                        new ResourceDescription("Secret.bin", () => new MemoryStream(privateData), isPublic: false),
                    ],
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(result.Success);
            }

            var resources = BuiltResources.ReadFrom(assembly);
            Assert.Equal(2, resources.Count);

            var roundTrip = compilation.Emit(new MemoryStream(), manifestResources: resources, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(roundTrip.Success);

            using var copy = new MemoryStream();
            compilation.Emit(copy, manifestResources: resources, cancellationToken: TestContext.Current.CancellationToken);
            var reread = Path.Combine(directory, "Copy.dll");
            File.WriteAllBytes(reread, copy.ToArray());
            var names = BuiltResources.ReadFrom(reread).Count;
            Assert.Equal(2, names);
            var loaded = System.Reflection.Assembly.Load(copy.ToArray());
            using var zones = loaded.GetManifestResourceStream("Data.Zones.nzd")!;
            using var reader = new StreamReader(zones);
            Assert.Equal("zone data", reader.ReadToEnd());
            Assert.Contains("Secret.bin", loaded.GetManifestResourceNames());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
