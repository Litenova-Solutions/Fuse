using Fuse.Fusion.Embeddings.Onnx;

namespace Fuse.Fusion.Embeddings.Onnx.Tests;

// Real-inference tests over the downloaded model. Each guards on the asset being present so the suite passes
// on a machine (or CI) without the ~90 MB download.
public sealed class OnnxEmbeddingModelTests
{
    [Fact]
    public void Embed_IsDeterministic_ForRankingStability()
    {
        using var model = LoadOrSkip();
        if (model is null)
            return;

        var first = model.Embed("process a customer payment");
        var second = model.Embed("process a customer payment");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Embed_ProducesNormalizedVectorOfModelDimension()
    {
        using var model = LoadOrSkip();
        if (model is null)
            return;

        var vector = model.Embed("charge the card");

        Assert.Equal(384, vector.Length);
        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        Assert.InRange(norm, 0.99, 1.01); // L2-normalized
    }

    [Fact]
    public void Embed_CapturesSemanticSimilarity_BeyondLexicalOverlap()
    {
        using var model = LoadOrSkip();
        if (model is null)
            return;

        var query = model.Embed("process a customer payment");
        var related = model.Embed("charge the buyer's credit card for the order");
        var unrelated = model.Embed("parse the XML configuration file at startup");

        // The related sentence shares almost no words with the query yet must score higher than the unrelated
        // one: this is the semantic signal the hashing embedding cannot provide.
        Assert.True(Cosine(query, related) > Cosine(query, unrelated),
            "expected the semantically related sentence to rank above the unrelated one");
    }

    private static OnnxEmbeddingModel? LoadOrSkip()
    {
        var dir = TestModel.LocalModelDirectory();
        if (dir is null)
            return null;

        var descriptor = EmbeddingModelDescriptor.Default;
        var resolved = new ResolvedEmbeddingModel(
            descriptor,
            Path.Combine(dir, descriptor.ModelFile.FileName),
            Path.Combine(dir, descriptor.VocabFile.FileName));
        return new OnnxEmbeddingModel(resolved);
    }

    private static double Cosine(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
            sum += a[i] * (double)b[i];
        return sum;
    }
}
