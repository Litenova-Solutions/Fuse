using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Fuse.BuildCaptureWorker.Tests;

// C4/tier-1: a captured compiler call records the strong-name key by a relative path ("../signing.snk") that does
// not resolve in the rehydration sandbox, so Roslyn reports CS7027 (an emit-output signing error) the real build
// never hit - and an empty signing key then breaks InternalsVisibleTo(PublicKey=...) matching, cascading into
// CS0281/CS0122 across a strong-named test project. Those are strong-name artifacts, not code errors: they must
// not drop a cleanly building repo below tier-1 nor false-red fuse_check. NormalizeSigning resolves the key file
// against the project directory when the checkout commits it (keeping signing correct so friend access matches),
// and clears signing only when the key is genuinely absent.
public sealed class BuildCaptureSigningTests
{
    private static CSharpCompilation SignedWith(string keyFile)
    {
        var tree = CSharpSyntaxTree.ParseText("namespace Sample; public sealed class Widget { public int Spin() => 42; }");
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithPublicSign(true)
            .WithCryptoKeyFile(keyFile);
        return CSharpCompilation.Create(
            "Sample",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            options);
    }

    [Fact]
    public void Missing_keyfile_produces_a_signing_error_before_normalizing()
    {
        // Guards the premise: without the fix the strong-name key error is present.
        var diagnostics = SignedWith("Z:/does/not/exist/signing.snk").GetDiagnostics();
        Assert.Contains(diagnostics, d => d.Id == "CS7027");
    }

    [Fact]
    public void Absent_key_is_cleared_so_no_signing_error_and_none_introduced()
    {
        // No project path and an unrooted, non-existent key -> fall back to clearing signing.
        var normalized = BuildCaptureRehydrator.NormalizeSigning(SignedWith("nope.snk"), projectFilePath: null);

        var diagnostics = normalized.GetDiagnostics();
        Assert.DoesNotContain(diagnostics, d => d.Id == "CS7027");
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        var options = Assert.IsType<CSharpCompilationOptions>(normalized.Options);
        Assert.Null(options.CryptoKeyFile);
        Assert.False(options.PublicSign);
    }

    [Fact]
    public void Committed_key_is_resolved_to_an_absolute_path_and_signing_is_kept()
    {
        // Simulate a repo that commits its key beside the project: a relative CryptoKeyFile that resolves against
        // the project directory. The fix keeps signing (so InternalsVisibleTo public-key matching still works) and
        // rewrites the key file to the resolved absolute path. NormalizeSigning only tests for the key's existence,
        // so a placeholder file exercises the resolve branch; end-to-end key validity is covered by the corpus run.
        var dir = Path.Combine(Path.GetTempPath(), "fuse-signing-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var keyPath = Path.Combine(dir, "signing.snk");
            File.WriteAllBytes(keyPath, [1, 2, 3, 4]);
            var projectFile = Path.Combine(dir, "Sample.csproj");

            var normalized = BuildCaptureRehydrator.NormalizeSigning(SignedWith("signing.snk"), projectFile);

            var options = Assert.IsType<CSharpCompilationOptions>(normalized.Options);
            Assert.Equal(Path.GetFullPath(keyPath), Path.GetFullPath(options.CryptoKeyFile!));
            Assert.True(options.PublicSign); // Signing kept (resolved), not cleared.
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
