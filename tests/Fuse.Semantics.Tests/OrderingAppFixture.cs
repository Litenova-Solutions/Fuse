using Fuse.Semantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Fuse.Semantics.Tests;

// Loads the OrderingApp fixture as an in-memory compilation (no MSBuild/restore). Framework stubs live in the
// fixture source, so the runtime reference set excludes assemblies that would redefine those namespaces
// (Microsoft.Extensions.*, Microsoft.AspNetCore.*, MediatR, xunit) to avoid ambiguous type definitions.
internal static class OrderingAppFixture
{
    private static readonly string[] ExcludedReferencePrefixes =
        ["Microsoft.Extensions", "Microsoft.AspNetCore", "MediatR", "xunit"];

    public static string RootDirectory => Locate();

    public static LoadedProject Load()
    {
        var root = Locate();
        var trees = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path))
            .ToList();

        var compilation = CSharpCompilation.Create(
            "OrderingApp",
            trees,
            ReferenceSet(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        return new LoadedProject("OrderingApp", Path.Combine(root, "OrderingApp.csproj"), "OrderingApp", compilation);
    }

    private static IReadOnlyList<MetadataReference> ReferenceSet()
    {
        var trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return trusted
            .Split(Path.PathSeparator)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Where(path => !ExcludedReferencePrefixes.Any(prefix =>
                Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
    }

    private static string Locate()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "tests", "fixtures")))
            dir = Path.GetDirectoryName(dir);

        Assert.NotNull(dir);
        return Path.Combine(dir!, "tests", "fixtures", "OrderingApp");
    }
}
