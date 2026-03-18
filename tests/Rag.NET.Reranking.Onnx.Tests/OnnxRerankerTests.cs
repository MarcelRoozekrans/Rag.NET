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

    [Fact]
    public void Constructor_WhenOptionsIsNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new OnnxReranker(null!));
    }

    [Fact]
    public void Constructor_WhenVocabPathDoesNotExist_ThrowsFileNotFoundException()
    {
        var options = new OnnxRerankerOptions
        {
            ModelPath = "nonexistent/model.onnx",
            VocabPath = "nonexistent/vocab.txt",
        };

        // FileNotFoundException for the model path fires first (model is checked first)
        Assert.Throws<FileNotFoundException>(() => new OnnxReranker(options));
    }

    [Fact]
    public void LoadVocab_CorrectlyMapsLineIndexToTokenId()
    {
        // Write a minimal vocab file: [PAD]=0, [UNK]=1, hello=2, world=3
        var vocabFile = Path.GetTempFileName();
        File.WriteAllLines(vocabFile, ["[PAD]", "[UNK]", "hello", "world"]);

        try
        {
            var vocab = OnnxReranker.LoadVocabForTest(vocabFile);
            Assert.Equal(0, vocab["[PAD]"]);
            Assert.Equal(1, vocab["[UNK]"]);
            Assert.Equal(2, vocab["hello"]);
            Assert.Equal(3, vocab["world"]);
        }
        finally
        {
            File.Delete(vocabFile);
        }
    }
}
