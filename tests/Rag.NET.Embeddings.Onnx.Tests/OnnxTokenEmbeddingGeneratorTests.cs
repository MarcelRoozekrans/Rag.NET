using Xunit;

namespace Rag.NET.Embeddings.Onnx.Tests;

public class OnnxTokenEmbeddingGeneratorTests
{
    [Fact]
    public void Constructor_WhenOptionsIsNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new OnnxTokenEmbeddingGenerator(null!));
    }

    [Fact]
    public void Constructor_WhenModelPathDoesNotExist_ThrowsFileNotFoundExceptionNamingThePath()
    {
        var options = new OnnxTokenEmbeddingOptions
        {
            ModelPath = "nonexistent/model.onnx",
            TokenizerVocabPath = "nonexistent/vocab.txt",
        };

        var ex = Assert.Throws<FileNotFoundException>(() => new OnnxTokenEmbeddingGenerator(options));
        Assert.Contains("nonexistent/model.onnx", ex.Message, StringComparison.Ordinal);
        Assert.Equal("nonexistent/model.onnx", ex.FileName);
    }

    [Fact]
    public void Constructor_WhenVocabPathDoesNotExist_ThrowsFileNotFoundExceptionNamingThePath()
    {
        // Use a real temp file as model path so the model check passes.
        var tempModel = Path.GetTempFileName();
        try
        {
            var options = new OnnxTokenEmbeddingOptions
            {
                ModelPath = tempModel,
                TokenizerVocabPath = "nonexistent/vocab.txt",
            };

            var ex = Assert.Throws<FileNotFoundException>(() => new OnnxTokenEmbeddingGenerator(options));
            Assert.Contains("nonexistent/vocab.txt", ex.Message, StringComparison.Ordinal);
            Assert.Equal("nonexistent/vocab.txt", ex.FileName);
        }
        finally
        {
            File.Delete(tempModel);
        }
    }

    [Theory]
    [InlineData(64, 64)]   // overlap == max
    [InlineData(64, 100)]  // overlap > max
    [InlineData(64, -1)]   // overlap negative
    public void Constructor_WhenWindowingInvalid_ThrowsArgumentOutOfRangeException(int maxTokens, int overlap)
    {
        var options = new OnnxTokenEmbeddingOptions
        {
            ModelPath = "nonexistent/model.onnx",
            TokenizerVocabPath = "nonexistent/vocab.txt",
            MaxTokens = maxTokens,
            WindowOverlapTokens = overlap,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new OnnxTokenEmbeddingGenerator(options));
    }
}
