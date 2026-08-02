using Microsoft.ML.Tokenizers;
using Rag.NET.Onnx.Bert;

namespace Rag.NET.Reranking.Onnx;

/// <summary>
/// Builds the <c>[CLS] query [SEP] passage [SEP]</c> feed a BERT cross-encoder scores, using the
/// same WordPiece tokenizer the ONNX embedders use.
/// </summary>
/// <remarks>
/// It has to be a real WordPiece tokenizer, not a whitespace split with a vocabulary lookup. A
/// lookup resolves only words the vocabulary lists verbatim, so every subword-composed term, every
/// word with punctuation attached and every number reaches the model as <c>[UNK]</c>. Measured
/// against ms-marco-MiniLM-L6-v2's own vocabulary over both corpora in full: 26.59% of SciFact's
/// 1,112,417 words and 17.62% of FiQA's 7,660,017, against 0.01% and 0.10% through WordPiece.
/// A cross-encoder whose input is a quarter <c>[UNK]</c> still returns varying scores, so it still
/// reorders every query — it just reorders them worse than the dense ranking it was handed, which
/// is why no reordering-based guard could see it.
/// </remarks>
internal sealed class CrossEncoderPairTokenizer
{
    /// <summary>[CLS] plus the two [SEP] positions every pair reserves.</summary>
    internal const int SpecialTokensPerPair = 3;

    private readonly BertTokenizer _tokenizer;

    internal CrossEncoderPairTokenizer(string vocabPath)
    {
        _tokenizer = BertWordPieceTokenization.CreateTokenizer(vocabPath);
    }

    /// <summary>
    /// Encodes one query/passage pair into ids, attention mask and segment ids, truncated to
    /// <paramref name="maxLength"/> positions in total.
    /// </summary>
    internal (long[] InputIds, long[] AttentionMask, long[] TokenTypeIds) Encode(
        string query, string passage, int maxLength)
    {
        // Special tokens are added here rather than by the tokenizer, so truncation cannot drop
        // the [SEP] the model expects at the end of each segment.
        var queryTokens = BertWordPieceTokenization.EncodeToTokens(_tokenizer, query, out _, out _);
        var passageTokens = BertWordPieceTokenization.EncodeToTokens(_tokenizer, passage, out _, out _);

        var (queryLength, passageLength) = SplitBudget(
            queryTokens.Count, passageTokens.Count, maxLength - SpecialTokensPerPair);

        return BuildFeed(queryTokens, queryLength, passageTokens, passageLength);
    }

    /// <summary>
    /// Divides <paramref name="available"/> content positions between the two segments: each keeps
    /// everything it has whenever the other side leaves the room, and only when BOTH want more
    /// than their half does either get cut to it.
    /// </summary>
    /// <remarks>
    /// Half is a ceiling only when the other side needs its own half. Capping the query at half
    /// unconditionally throws query text away while positions the passage never asked for go
    /// unused: a 400-piece query against a 10-piece passage at MaxLength 512 was cut to 254 pieces
    /// and fed as a 267-position sequence — 146 pieces of the query discarded and 245 positions
    /// left empty. Neither side may take the other's half outright either, or a long query would
    /// starve the passage the model is being asked to score.
    /// </remarks>
    internal static (int QueryLength, int PassageLength) SplitBudget(
        int queryTokenCount, int passageTokenCount, int available)
    {
        if (available <= 0)
            return (0, 0);

        var queryShare = available / 2;
        var passageShare = available - queryShare;

        if (queryTokenCount <= queryShare)
            return (queryTokenCount, Math.Min(passageTokenCount, available - queryTokenCount));

        if (passageTokenCount <= passageShare)
            return (Math.Min(queryTokenCount, available - passageTokenCount), passageTokenCount);

        return (queryShare, passageShare);
    }

    /// <summary>
    /// Lays the two truncated segments out as <c>[CLS] query [SEP] passage [SEP]</c>, with the
    /// segment ids 0 through the first [SEP] and 1 from there on.
    /// </summary>
    private (long[] InputIds, long[] AttentionMask, long[] TokenTypeIds) BuildFeed(
        IReadOnlyList<EncodedToken> queryTokens,
        int queryLength,
        IReadOnlyList<EncodedToken> passageTokens,
        int passageLength)
    {
        var totalLength = queryLength + passageLength + SpecialTokensPerPair;
        var inputIds = new long[totalLength];
        var attentionMask = new long[totalLength];
        var tokenTypeIds = new long[totalLength];

        // Every position carries a real token: the feed is a single unpadded sequence.
        Array.Fill(attentionMask, 1L);

        inputIds[0] = _tokenizer.ClassificationTokenId;

        var position = 1;
        for (var i = 0; i < queryLength; i++, position++)
            inputIds[position] = queryTokens[i].Id;

        inputIds[position] = _tokenizer.SeparatorTokenId;
        position++;

        for (var i = 0; i < passageLength; i++, position++)
        {
            inputIds[position] = passageTokens[i].Id;
            tokenTypeIds[position] = 1;
        }

        inputIds[position] = _tokenizer.SeparatorTokenId;
        tokenTypeIds[position] = 1;

        return (inputIds, attentionMask, tokenTypeIds);
    }
}
