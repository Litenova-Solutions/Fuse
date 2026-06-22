namespace Fuse.Fusion.Embeddings.Onnx;

/// <summary>
///     The pinned identity of a downloadable sentence-encoder model: its files, source URLs, expected SHA-256
///     hashes, and vector dimensionality. Hashes are verified after download and a mismatch is rejected.
/// </summary>
/// <param name="Name">The model name, used as the per-machine cache subdirectory.</param>
/// <param name="Dimensions">The embedding vector length the model produces.</param>
/// <param name="MaxTokens">The maximum input token length; longer inputs are truncated.</param>
/// <param name="ModelFile">The ONNX weights file descriptor.</param>
/// <param name="VocabFile">The WordPiece vocabulary file descriptor.</param>
public sealed record EmbeddingModelDescriptor(
    string Name,
    int Dimensions,
    int MaxTokens,
    EmbeddingModelFile ModelFile,
    EmbeddingModelFile VocabFile)
{
    /// <summary>
    ///     The default model: <c>all-MiniLM-L6-v2</c>, a 384-dimension sentence encoder (~90 MB). URLs and
    ///     hashes were captured from the Hugging Face <c>sentence-transformers/all-MiniLM-L6-v2</c> repository.
    /// </summary>
    public static EmbeddingModelDescriptor Default { get; } = new(
        "all-MiniLM-L6-v2",
        Dimensions: 384,
        MaxTokens: 256,
        new EmbeddingModelFile(
            "model.onnx",
            "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx",
            "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452",
            90405214),
        new EmbeddingModelFile(
            "vocab.txt",
            "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt",
            "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3",
            231508));
}

/// <summary>
///     One downloadable file belonging to an <see cref="EmbeddingModelDescriptor" />.
/// </summary>
/// <param name="FileName">The file name within the model cache directory.</param>
/// <param name="Url">The pinned download URL.</param>
/// <param name="Sha256">The lowercase hexadecimal SHA-256 the downloaded bytes must match.</param>
/// <param name="SizeBytes">The expected file size in bytes, used only for the download notice.</param>
public sealed record EmbeddingModelFile(string FileName, string Url, string Sha256, long SizeBytes);
