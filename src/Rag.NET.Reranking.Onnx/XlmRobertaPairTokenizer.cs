using Microsoft.ML.Tokenizers;

namespace Rag.NET.Reranking.Onnx;

/// <summary>
/// Builds the <c>&lt;s&gt; query &lt;/s&gt;&lt;/s&gt; passage &lt;/s&gt;</c> feed an XLM-RoBERTa cross-encoder
/// scores, tokenizing with the SentencePiece model the checkpoint was trained with.
/// </summary>
/// <remarks>
/// The SentencePiece model and the model's embedding matrix number their pieces differently. The
/// <c>.model</c> file puts <c>&lt;unk&gt;</c>, <c>&lt;s&gt;</c> and <c>&lt;/s&gt;</c> at 0, 1 and 2; the checkpoint
/// follows fairseq, which puts <c>&lt;s&gt;</c>, <c>&lt;pad&gt;</c>, <c>&lt;/s&gt;</c> and <c>&lt;unk&gt;</c> at 0 to 3 and
/// shifts every other piece up by one. Feeding the raw ids is silent: they are all valid row
/// indices, so the model scores a sentence made of the wrong words and returns plausible-looking
/// numbers instead of failing. <see cref="MapRawId"/> applies the shift, and the offset is pinned
/// against the model's own tokenizer in the tests.
/// </remarks>
internal sealed class XlmRobertaPairTokenizer : IPairTokenizer
{
    /// <summary>&lt;s&gt; plus the two &lt;/s&gt; between the segments and the one closing the pair.</summary>
    internal const int SpecialTokensPerPair = 4;

    internal const long BosId = 0;
    internal const long EosId = 2;
    internal const long UnkId = 3;

    private readonly SentencePieceTokenizer _tokenizer;

    internal XlmRobertaPairTokenizer(string tokenizerPath)
    {
        using var stream = File.OpenRead(tokenizerPath);
        _tokenizer = SentencePieceTokenizer.Create(
            stream, addBeginningOfSentence: false, addEndOfSentence: false);
    }

    /// <inheritdoc />
    public (long[] InputIds, long[] AttentionMask, long[] TokenTypeIds) Encode(
        string query, string passage, int maxLength)
    {
        // Truncation happens in BuildFeed, not in the tokenizer, so the closing </s> survives it.
        var queryIds = _tokenizer.EncodeToIds(query);
        var passageIds = _tokenizer.EncodeToIds(passage);
        return BuildFeed(queryIds, passageIds, maxLength);
    }

    /// <summary>Maps an id from the SentencePiece model to the checkpoint's fairseq numbering.</summary>
    internal static long MapRawId(int rawId) => rawId switch
    {
        0 => UnkId,
        1 => BosId,
        2 => EosId,
        _ => rawId + 1L,
    };

    /// <summary>
    /// Lays the two segments out as <c>&lt;s&gt; query &lt;/s&gt;&lt;/s&gt; passage &lt;/s&gt;</c>, truncated through
    /// <see cref="CrossEncoderPairTokenizer.SplitBudget"/>. XLM-RoBERTa has no segment embeddings in
    /// use, so the token types are all 0 and every position is attended.
    /// </summary>
    internal static (long[] InputIds, long[] AttentionMask, long[] TokenTypeIds) BuildFeed(
        IReadOnlyList<int> rawQueryIds, IReadOnlyList<int> rawPassageIds, int maxLength)
    {
        var (queryLength, passageLength) = CrossEncoderPairTokenizer.SplitBudget(
            rawQueryIds.Count, rawPassageIds.Count, maxLength - SpecialTokensPerPair);

        var totalLength = queryLength + passageLength + SpecialTokensPerPair;
        var inputIds = new long[totalLength];
        var attentionMask = new long[totalLength];
        var tokenTypeIds = new long[totalLength];

        Array.Fill(attentionMask, 1L);

        var position = 0;
        inputIds[position++] = BosId;
        for (var i = 0; i < queryLength; i++)
            inputIds[position++] = MapRawId(rawQueryIds[i]);
        inputIds[position++] = EosId;
        inputIds[position++] = EosId;
        for (var i = 0; i < passageLength; i++)
            inputIds[position++] = MapRawId(rawPassageIds[i]);
        inputIds[position] = EosId;

        return (inputIds, attentionMask, tokenTypeIds);
    }
}
