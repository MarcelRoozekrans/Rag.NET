using Xunit;

namespace Rag.NET.Reranking.Onnx.Tests;

/// <summary>
/// Pins the XLM-RoBERTa pair feed: the fairseq id offset, the <c>&lt;s&gt; q &lt;/s&gt;&lt;/s&gt; p &lt;/s&gt;</c>
/// layout and the position budget.
/// </summary>
public sealed class XlmRobertaPairTokenizerTests
{
    [Theory]
    [InlineData(0, 3)]  // <unk>
    [InlineData(1, 0)]  // <s>
    [InlineData(2, 2)]  // </s>
    [InlineData(3, 4)]
    [InlineData(6585, 6586)] // "▁Wat", per the model's tokenizer.json
    public void MapRawId_AppliesTheFairseqOffset(int raw, long expected)
        => Assert.Equal(expected, XlmRobertaPairTokenizer.MapRawId(raw));

    [Fact]
    public void BuildFeed_LaysThePairOutAsTheXlmRobertaTemplate()
    {
        var (ids, mask, types) = XlmRobertaPairTokenizer.BuildFeed([10, 11], [20], maxLength: 512);
        Assert.Equal([0L, 11, 12, 2, 2, 21, 2], ids);
        Assert.All(mask, m => Assert.Equal(1L, m));
        Assert.All(types, t => Assert.Equal(0L, t));
        Assert.Equal(ids.Length, mask.Length);
        Assert.Equal(ids.Length, types.Length);
    }

    [Fact]
    public void BuildFeed_WithAnEmptyQuery_StillProducesAValidFeed()
        => Assert.Equal([0L, 2, 2, 21, 2], XlmRobertaPairTokenizer.BuildFeed([], [20], 512).InputIds);

    [Fact]
    public void BuildFeed_WithAShortQueryAndALongPassage_KeepsTheQueryWholeAndTruncatesThePassage()
    {
        var passage = Enumerable.Range(100, 1000).ToArray();

        var ids = XlmRobertaPairTokenizer.BuildFeed([10, 11, 12], passage, maxLength: 16).InputIds;

        // 16 positions less the 4 special tokens leaves 12; the query keeps its 3, the passage gets 9.
        Assert.Equal(
            [0L, 11, 12, 13, 2, 2, 101, 102, 103, 104, 105, 106, 107, 108, 109, 2],
            ids);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(16)]
    [InlineData(512)]
    public void BuildFeed_NeverProducesMorePositionsThanMaxLength(int maxLength)
    {
        var longSide = Enumerable.Range(3, 1000).ToArray();
        var ids = XlmRobertaPairTokenizer.BuildFeed(longSide, longSide, maxLength).InputIds;
        Assert.True(ids.Length <= maxLength);
        Assert.Equal(0L, ids[0]);
        Assert.Equal(2L, ids[^1]);
    }

    /// <summary>
    /// Parity with the model's own tokenizer. Expected ids come from the Hugging Face
    /// <c>tokenizers</c> package loading <c>tokenizer.json</c> of <c>BAAI/bge-reranker-v2-m3</c> at
    /// commit <c>953dc6f6f85a1b2dbfca4c34a2796e7dde08d41e</c>; the <c>sentencepiece.bpe.model</c>
    /// under test is from the same commit. Skipped unless
    /// <c>RAGNET_ONNX_RERANK_TOKENIZER</c> points at that file, because the model is not committed.
    /// </summary>
    [Fact]
    public void Encode_MatchesTheIdsTheModelsOwnTokenizerGives()
    {
        var path = Environment.GetEnvironmentVariable("RAGNET_ONNX_RERANK_TOKENIZER");
        Assert.SkipWhen(string.IsNullOrEmpty(path) || !File.Exists(path),
            "Set RAGNET_ONNX_RERANK_TOKENIZER to bge-reranker-v2-m3's sentencepiece.bpe.model to run this test.");

        var ids = new XlmRobertaPairTokenizer(path!)
            .Encode("Wat is de hoofdstad van Nederland?", "Amsterdam is de hoofdstad van Nederland.", 512).InputIds;

        Assert.Equal(
            [0L, 6586, 83, 8, 35912, 5481, 131, 13666, 32, 2, 2, 21391, 83, 8, 35912, 5481, 131, 13666, 5, 2],
            ids);
    }
}
