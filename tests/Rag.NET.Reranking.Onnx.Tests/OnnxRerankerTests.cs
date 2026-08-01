using Xunit;

namespace Rag.NET.Reranking.Onnx.Tests;

public class OnnxRerankerTests
{
    [Fact]
    public void Constructor_WhenModelPathDoesNotExist_ThrowsFileNotFoundException()
    {
        var options = new OnnxRerankerOptions
        {
            ModelPath = "nonexistent/model.onnx",
            VocabPath = "nonexistent/vocab.txt",
        };

        Assert.Throws<FileNotFoundException>(() => new OnnxReranker(options));
    }

    /// <summary>
    /// MaxLength is the ceiling on the whole feed, and [CLS] plus two [SEP] already occupy three
    /// positions. A MaxLength that cannot hold them leaves no truncation that honours it, so it is
    /// refused at construction rather than silently producing a longer sequence than asked for.
    /// Checked before the file paths are, so the message names the option and not a missing file.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Constructor_WhenMaxLengthCannotHoldTheSpecialTokens_ThrowsArgumentOutOfRangeException(int maxLength)
    {
        var options = new OnnxRerankerOptions
        {
            ModelPath = "nonexistent/model.onnx",
            VocabPath = "nonexistent/vocab.txt",
            MaxLength = maxLength,
        };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new OnnxReranker(options));
        Assert.Contains("MaxLength", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_WhenOptionsIsNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new OnnxReranker(null!));
    }

    [Fact]
    public void Constructor_WhenVocabPathDoesNotExist_ThrowsFileNotFoundException()
    {
        // Use a real temp file as model path so the model check passes
        var tempModel = Path.GetTempFileName();
        try
        {
            var options = new OnnxRerankerOptions
            {
                ModelPath = tempModel,
                VocabPath = "nonexistent/vocab.txt",
            };

            var ex = Assert.Throws<FileNotFoundException>(() => new OnnxReranker(options));
            Assert.Contains("vocab", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tempModel);
        }
    }
}
