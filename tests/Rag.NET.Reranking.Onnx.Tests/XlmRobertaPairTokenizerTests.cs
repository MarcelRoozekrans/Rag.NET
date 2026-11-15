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

    [Fact]
    public void FuseUnknownRuns_CollapsesARunIntoOne()
        => Assert.Equal([5, 0, 7], XlmRobertaPairTokenizer.FuseUnknownRuns([5, 0, 0, 0, 7]));

    [Fact]
    public void FuseUnknownRuns_KeepsUnknownsSeparatedByAnotherPieceApart()
        => Assert.Equal([0, 5, 0], XlmRobertaPairTokenizer.FuseUnknownRuns([0, 0, 5, 0, 0]));

    [Fact]
    public void FuseUnknownRuns_CountsTowardsTheTruncationBudgetAsOne()
    {
        // 10 unknowns fuse to 1, so with a budget of 3 for the passage everything fits untruncated.
        int[] passage = [.. Enumerable.Repeat(0, 10), 8, 9];
        var ids = XlmRobertaPairTokenizer.BuildFeed(
            [], XlmRobertaPairTokenizer.FuseUnknownRuns(passage), maxLength: 7).InputIds;
        Assert.Equal([0L, 2, 2, 3, 9, 10, 2], ids);
    }

    public static TheoryData<char> DroppedControlCharacters()
    {
        var data = new TheoryData<char>();
        for (var c = '\u0001'; c <= '\u0008'; c++) data.Add(c);
        data.Add('\u000b');
        for (var c = '\u000e'; c <= '\u001f'; c++) data.Add(c);
        data.Add('\u007f');
        data.Add('\u008f');
        data.Add('\u009f');
        return data;
    }

    [Fact]
    public void DroppedControlCharacters_AreExactlyThirty()
        => Assert.Equal(30, DroppedControlCharacters().Count);

    [Theory]
    [MemberData(nameof(DroppedControlCharacters))]
    public void StripDroppedControlCharacters_RemovesEachDroppedCharacter(char c)
        => Assert.Equal("ab", XlmRobertaPairTokenizer.StripDroppedControlCharacters($"a{c}b"));

    [Fact]
    public void StripDroppedControlCharacters_KeepsTabAndNewline()
        => Assert.Equal("a\tb\nc\rd\fe", XlmRobertaPairTokenizer.StripDroppedControlCharacters("a\tb\nc\rd\fe"));

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

    /// <summary>
    /// Single-segment parity, with ids recorded from tokenizer.json and cross-checked against the
    /// reference sentencepiece library. Covers unknown-run fusing and dropped control characters.
    /// </summary>
    [Theory]
    [InlineData("emoji \U0001F99C\U0001F99C test", new long[] { 28, 121505, 6, 3, 3034 })]
    [InlineData("a \U0001F99C b \U0001F99C\U0001F99C\U0001F99C c", new long[] { 10, 6, 3, 876, 6, 3, 501 })]
    [InlineData("control\u0001char", new long[] { 6226, 9254 })]
    [InlineData("x\u0003\u0004y", new long[] { 1022, 53 })]
    public void EncodeSegment_MatchesTheModelsOwnTokenizer(string text, long[] expected)
    {
        var path = Environment.GetEnvironmentVariable("RAGNET_ONNX_RERANK_TOKENIZER");
        Assert.SkipWhen(string.IsNullOrEmpty(path) || !File.Exists(path),
            "Set RAGNET_ONNX_RERANK_TOKENIZER to bge-reranker-v2-m3's sentencepiece.bpe.model to run this test.");

        Assert.Equal(expected, new XlmRobertaPairTokenizer(path!).EncodeSegment(text));
    }
}
