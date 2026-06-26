using Fuse.Semantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Fuse.Semantics.Tests;

// Compiles inline test source together with the OrderingApp fixture's Framework.cs stubs (MediatR, MVC,
// Microsoft.Extensions.*), so analyzer tests can use short, focused snippets. Uses the same reference set as
// OrderingAppFixture (TPA minus the stubbed-namespace assemblies) to avoid ambiguous type definitions.
internal static class InlineCompilation
{
    private static readonly string[] ExcludedReferencePrefixes =
        ["Microsoft.Extensions", "Microsoft.AspNetCore", "MediatR", "xunit"];

    public static LoadedProject Load(params string[] sources)
    {
        var frameworkPath = Path.Combine(OrderingAppFixture.RootDirectory, "Framework.cs");
        var trees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(File.ReadAllText(frameworkPath), path: frameworkPath),
        };
        for (var i = 0; i < sources.Length; i++)
            trees.Add(CSharpSyntaxTree.ParseText(sources[i], path: $"/repo/src/Inline{i}.cs"));

        var compilation = CSharpCompilation.Create(
            "Inline",
            trees,
            ReferenceSet(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        return new LoadedProject("Inline", "/repo/Inline.csproj", "Inline", compilation);
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
}
