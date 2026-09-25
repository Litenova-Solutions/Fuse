using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Testing;

/// <summary>Recognizes test methods across xUnit, NUnit, MSTest and TUnit.</summary>
internal static class TestAttributes
{
    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "Fact", "Theory", "Test", "TestCase", "TestCaseSource", "TestMethod", "DataTestMethod", "SkippableFact", "SkippableTheory",
    };

    /// <summary>True when the method carries a test attribute or an attribute derived from one.</summary>
    public static bool IsTest(IMethodSymbol method)
    {
        foreach (var attribute in method.GetAttributes())
        {
            for (var type = attribute.AttributeClass; type is not null; type = type.BaseType)
            {
                var name = type.Name.EndsWith("Attribute", StringComparison.Ordinal) ? type.Name[..^"Attribute".Length] : type.Name;
                if (Names.Contains(name))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Syntactic check, for counting tests without binding.</summary>
    public static bool LooksLikeTest(MethodDeclarationSyntax method) =>
        method.AttributeLists.SelectMany(l => l.Attributes).Any(a =>
        {
            var name = a.Name switch
            {
                QualifiedNameSyntax q => q.Right.Identifier.Text,
                AliasQualifiedNameSyntax q => q.Name.Identifier.Text,
                SimpleNameSyntax s => s.Identifier.Text,
                _ => a.Name.ToString(),
            };
            if (name.EndsWith("Attribute", StringComparison.Ordinal))
                name = name[..^"Attribute".Length];
            return Names.Contains(name);
        });
}
