using Fuse.Fusion.Embeddings.Onnx;

namespace Fuse.Fusion.Embeddings.Onnx.Tests;

// Locates the default embedding model in the per-machine cache (or a sideload path) so the real-inference
// tests can run when the asset is present and skip cleanly when it is not (for example CI without the download).
internal static class TestModel
{
    public static string? LocalModelDirectory()
    {
        var descriptor = EmbeddingModelDescriptor.Default;
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fuse", "models", descriptor.Name);

        var model = Path.Combine(cacheDir, descriptor.ModelFile.FileName);
        var vocab = Path.Combine(cacheDir, descriptor.VocabFile.FileName);
        return File.Exists(model) && File.Exists(vocab) ? cacheDir : null;
    }
}
