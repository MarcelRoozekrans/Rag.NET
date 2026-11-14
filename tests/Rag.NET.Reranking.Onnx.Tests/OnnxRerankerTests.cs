using Xunit;

namespace Rag.NET.Reranking.Onnx.Tests;

public class OnnxRerankerTests
{
    [Theory]
    [InlineData("input_ids", "attention_mask", "token_type_ids")]
    [InlineData("input_ids", "attention_mask")]
    public void DescribeInputProblem_WhenInputsAreSupported_ReturnsNull(params string[] declared)
    {
        Assert.Null(OnnxReranker.DescribeInputProblem(declared));
    }

    [Fact]
    public void DescribeInputProblem_WhenOneInputIsUnsupported_NamesItInTheSingular()
    {
        var message = OnnxReranker.DescribeInputProblem(["input_ids", "position_ids"]);

        Assert.Equal(
            "The ONNX model declares input 'position_ids', but the reranker supplies only "
            + "input_ids, attention_mask, token_type_ids.",
            message);
    }

    [Fact]
    public void DescribeInputProblem_WhenSeveralInputsAreUnsupported_NamesAllOfThemSorted()
    {
        var message = OnnxReranker.DescribeInputProblem(["input_ids", "zeta", "alpha"]);

        Assert.Equal(
            "The ONNX model declares inputs 'alpha', 'zeta', but the reranker supplies only "
            + "input_ids, attention_mask, token_type_ids.",
            message);
    }

    [Fact]
    public void DescribeInputProblem_WhenInputIdsIsMissing_IsRejected()
    {
        var message = OnnxReranker.DescribeInputProblem(["attention_mask"]);

        Assert.NotNull(message);
        Assert.Contains("does not declare input 'input_ids'", message, StringComparison.Ordinal);
    }

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

    [Fact]
    public void Constructor_WhenBothVocabPathAndTokenizerPathAreSet_ThrowsArgumentException()
    {
        var options = new OnnxRerankerOptions
        {
            ModelPath = "nonexistent/model.onnx",
            VocabPath = "nonexistent/vocab.txt",
            TokenizerPath = "nonexistent/sentencepiece.bpe.model",
        };

        var ex = Assert.Throws<ArgumentException>(() => new OnnxReranker(options));
        Assert.Contains("VocabPath", ex.Message, StringComparison.Ordinal);
        Assert.Contains("TokenizerPath", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData(" ", null)]
    [InlineData("", "")]
    [InlineData(" ", "")]
    public void Constructor_WhenNeitherVocabPathNorTokenizerPathIsSet_ThrowsArgumentException(
        string vocabPath, string? tokenizerPath)
    {
        var options = new OnnxRerankerOptions
        {
            ModelPath = "nonexistent/model.onnx",
            VocabPath = vocabPath,
            TokenizerPath = tokenizerPath,
        };

        var ex = Assert.Throws<ArgumentException>(() => new OnnxReranker(options));
        Assert.Contains("VocabPath", ex.Message, StringComparison.Ordinal);
        Assert.Contains("TokenizerPath", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_WhenTokenizerPathIsWhitespace_UsesVocabPath()
    {
        var tempModel = Path.GetTempFileName();
        try
        {
            var options = new OnnxRerankerOptions
            {
                ModelPath = tempModel,
                VocabPath = "nonexistent/vocab.txt",
                TokenizerPath = "   ",
            };

            var ex = Assert.Throws<FileNotFoundException>(() => new OnnxReranker(options));
            Assert.Equal("nonexistent/vocab.txt", ex.FileName);
        }
        finally
        {
            File.Delete(tempModel);
        }
    }

    /// <summary>
    /// The XLM-RoBERTa feed reserves four positions (&lt;s&gt;, two &lt;/s&gt; between the segments, one closing),
    /// so a MaxLength the BERT path accepts can still be too small for it.
    /// </summary>
    [Fact]
    public void Constructor_WhenMaxLengthCannotHoldTheXlmRobertaSpecialTokens_ThrowsArgumentOutOfRangeException()
    {
        var options = new OnnxRerankerOptions
        {
            ModelPath = "nonexistent/model.onnx",
            TokenizerPath = "nonexistent/sentencepiece.bpe.model",
            MaxLength = 4,
        };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new OnnxReranker(options));
        Assert.Contains("MaxLength", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_WhenMaxLengthIsFourOnTheBertPath_PassesTheLengthCheck()
    {
        var options = new OnnxRerankerOptions
        {
            ModelPath = "nonexistent/model.onnx",
            VocabPath = "nonexistent/vocab.txt",
            MaxLength = 4,
        };

        Assert.Throws<FileNotFoundException>(() => new OnnxReranker(options));
    }

    [Fact]
    public void Constructor_WhenTokenizerPathDoesNotExist_ThrowsFileNotFoundException()
    {
        var tempModel = Path.GetTempFileName();
        try
        {
            var options = new OnnxRerankerOptions
            {
                ModelPath = tempModel,
                TokenizerPath = "nonexistent/sentencepiece.bpe.model",
            };

            var ex = Assert.Throws<FileNotFoundException>(() => new OnnxReranker(options));
            Assert.Equal("nonexistent/sentencepiece.bpe.model", ex.FileName);
        }
        finally
        {
            File.Delete(tempModel);
        }
    }

    [Fact]
    public void Constructor_WhenTokenizerPathIsNotASentencePieceModel_ThrowsInvalidDataException()
    {
        var tempModel = Path.GetTempFileName();
        var tempTokenizer = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempTokenizer, "{}");
            var options = new OnnxRerankerOptions { ModelPath = tempModel, TokenizerPath = tempTokenizer };

            var ex = Assert.Throws<InvalidDataException>(() => new OnnxReranker(options));
            Assert.Contains("TokenizerPath", ex.Message, StringComparison.Ordinal);
            Assert.Contains(tempTokenizer, ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(tempModel);
            File.Delete(tempTokenizer);
        }
    }
}
