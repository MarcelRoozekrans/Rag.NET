using Xunit;

namespace Rag.NET.Reranking.Onnx.Tests;

/// <summary>
/// Pins that the cross-encoder feed is built by a real WordPiece tokenizer.
/// </summary>
/// <remarks>
/// <para>
/// The guard the ablation table did not have. <c>OnnxReranker</c> shipped with a tokenizer that
/// whitespace-split the text and looked each whole lowercased word up in the vocabulary, so every
/// word not listed verbatim — every subword-composed term, every word with punctuation stuck to
/// it, every number — reached the model as <c>[UNK]</c>. Measured against the cross-encoder's own
/// vocabulary over both corpora in full, that was 26.59% of SciFact's words and 17.62% of FiQA's
/// (0.01% and 0.10% through WordPiece), and it cost the reranked ablation row 0.117 nDCG@10 on
/// SciFact: it scored 0.0790 BELOW the dense row it was supposed to improve, where the same model
/// with proper WordPiece scores 0.0385 above it.
/// </para>
/// <para>
/// Neither existing guard could catch that. <c>RerankedAblationRow.AssertRerankerReordered</c>
/// checks that the cross-encoder MOVED the ranking, and garbage-but-varying scores reorder every
/// query — which is exactly what it observed. The reranker smoke test used common whole words,
/// which survive a naive tokenizer intact. So the guard has to target the tokenizer directly, and
/// it has to use vocabulary the naive path destroys.
/// </para>
/// <para>
/// Offline by construction: a hand-written WordPiece vocabulary, no ONNX model and no download, so
/// this runs on every build rather than only under the nightly measurement gate. The vocabulary
/// deliberately places <c>[UNK]</c>, <c>[CLS]</c> and <c>[SEP]</c> at 1, 2 and 3 instead of the
/// standard 100, 101 and 102, which is what pins that the special-token ids are READ from the
/// vocabulary rather than hardcoded.
/// </para>
/// </remarks>
public sealed class CrossEncoderPairTokenizerTests : IDisposable
{
    /// <summary>
    /// A WordPiece vocabulary: whole words, continuation pieces (<c>##</c>) and punctuation as
    /// its own token. Line index is token id.
    /// </summary>
    private static readonly string[] VocabLines =
    [
        "[PAD]",      // 0
        "[UNK]",      // 1  — deliberately NOT 100
        "[CLS]",      // 2  — deliberately NOT 101
        "[SEP]",      // 3  — deliberately NOT 102
        "[MASK]",     // 4
        ".",          // 5
        ",",          // 6
        "the",        // 7
        "of",         // 8
        "and",        // 9
        "in",         // 10
        "dog",        // 11
        "cell",       // 12
        "micro",      // 13
        "structure",  // 14
        "0",          // 15
        "1",          // 16
        "2",          // 17
        "3",          // 18
        "5",          // 19
        "##tub",      // 20
        "##ule",      // 21
        "##s",        // 22
        "##ular",     // 23
        "##0",        // 24
        "##1",        // 25
        "##2",        // 26
        "##3",        // 27
        "##5",        // 28
    ];

    private const int UnknownId = 1;
    private const int ClassificationId = 2;
    private const int SeparatorId = 3;

    private readonly string _vocabPath;

    public CrossEncoderPairTokenizerTests()
    {
        _vocabPath = Path.Combine(
            Path.GetTempPath(), "ragnet-reranker-vocab-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllLines(_vocabPath, VocabLines);
    }

    public void Dispose()
    {
        if (File.Exists(_vocabPath))
        {
            File.Delete(_vocabPath);
        }
    }

    /// <summary>
    /// THE guard. Every content position must carry a real vocabulary id — not <c>[UNK]</c>, and
    /// not an id the vocabulary does not contain at all. Each term here is one a whitespace
    /// splitter cannot resolve: <c>microtubule</c> is only in the vocabulary as three pieces,
    /// <c>dog.</c> and <c>cells</c> carry punctuation and a suffix, and <c>3,520,031</c> is a
    /// number the vocabulary holds only digit by digit.
    /// </summary>
    [Fact]
    public void Encode_ResolvesSubwordTermsAndPunctuationAgainstTheVocabulary_InsteadOfMappingThemToUnknown()
    {
        var tokenizer = new CrossEncoderPairTokenizer(_vocabPath);

        var (inputIds, _, tokenTypeIds) = tokenizer.Encode(
            "microtubule dog.", "3,520,031 cells in the cell.", maxLength: 64);

        var unresolved = UnresolvedContentTokens(inputIds, tokenTypeIds);
        Assert.Empty(unresolved);
    }

    /// <summary>
    /// The round trip: a word absent from the vocabulary comes back as the pieces that spell it,
    /// and punctuation is its own token rather than glued to the word before it. The exact
    /// sequence also pins that <c>[CLS]</c> and <c>[SEP]</c> are the vocabulary's own ids.
    /// </summary>
    [Fact]
    public void Encode_SplitsAnUnlistedWordIntoItsWordPiecePieces_AndSeparatesPunctuation()
    {
        var tokenizer = new CrossEncoderPairTokenizer(_vocabPath);

        var (inputIds, _, _) = tokenizer.Encode("microtubule", "dog.", maxLength: 64);

        Assert.Equal(
            ["[CLS]", "micro", "##tub", "##ule", "[SEP]", "dog", ".", "[SEP]"],
            Decode(inputIds));
    }

    /// <summary>
    /// The special-token ids come from the vocabulary file, not from the standard BERT constants.
    /// This vocabulary puts them at 2 and 3; hardcoded 101 and 102 are not even valid ids in it.
    /// </summary>
    [Fact]
    public void Encode_ReadsTheSpecialTokenIdsFromTheVocabulary()
    {
        var tokenizer = new CrossEncoderPairTokenizer(_vocabPath);

        var (inputIds, _, tokenTypeIds) = tokenizer.Encode("the dog", "the cell", maxLength: 64);

        Assert.Equal(ClassificationId, inputIds[0]);
        Assert.Equal(SeparatorId, inputIds[FirstSeparatorIndex(tokenTypeIds)]);
        Assert.Equal(SeparatorId, inputIds[^1]);
    }

    /// <summary>The mask marks every position real, and the segment ids split at the first [SEP].</summary>
    [Fact]
    public void Encode_MarksEveryPositionReal_AndSegmentsAtTheFirstSeparator()
    {
        var tokenizer = new CrossEncoderPairTokenizer(_vocabPath);

        var (inputIds, attentionMask, tokenTypeIds) = tokenizer.Encode("the dog", "the cell", maxLength: 64);

        Assert.Equal(inputIds.Length, attentionMask.Length);
        Assert.Equal(inputIds.Length, tokenTypeIds.Length);
        Assert.All(attentionMask, mask => Assert.Equal(1, mask));

        var firstSeparator = FirstSeparatorIndex(tokenTypeIds);
        for (var i = 0; i < inputIds.Length; i++)
        {
            Assert.Equal(i <= firstSeparator ? 0 : 1, tokenTypeIds[i]);
        }
    }

    /// <summary>
    /// A passage that fits inside its half of the budget does not lock the unused half away from
    /// the query. Six query pieces and two passage pieces into a budget of nine: the passage takes
    /// the two it needs and the query keeps all six, rather than being cut to four so that three
    /// positions can go unused and two of the query's own pieces be thrown away.
    /// </summary>
    [Fact]
    public void Encode_WhenThePassageDoesNotNeedItsHalf_GivesTheQueryTheRoomInsteadOfWastingIt()
    {
        var tokenizer = new CrossEncoderPairTokenizer(_vocabPath);

        // "microtubule cells in" is micro ##tub ##ule cell ##s in; "the dog" is the dog.
        var (inputIds, _, tokenTypeIds) = tokenizer.Encode("microtubule cells in", "the dog", maxLength: 12);

        Assert.Equal(6, QueryLength(tokenTypeIds));
        Assert.Equal(2, PassageLength(inputIds, tokenTypeIds));
        Assert.Equal(11, inputIds.Length);
    }

    /// <summary>
    /// The mirror case, which already held and must keep holding: a short query does not stop the
    /// passage from using the rest of the budget.
    /// </summary>
    [Fact]
    public void Encode_WhenTheQueryDoesNotNeedItsHalf_GivesThePassageTheRoom()
    {
        var tokenizer = new CrossEncoderPairTokenizer(_vocabPath);

        // "the dog" is 2 pieces; "microtubule cells in" is 6, of which 7 positions are on offer.
        var (inputIds, _, tokenTypeIds) = tokenizer.Encode("the dog", "microtubule cells in", maxLength: 12);

        Assert.Equal(2, QueryLength(tokenTypeIds));
        Assert.Equal(6, PassageLength(inputIds, tokenTypeIds));
        Assert.Equal(11, inputIds.Length);
    }

    /// <summary>
    /// When both sides want more than half, neither may take the other's share — a long query must
    /// not starve the passage down to nothing. Six query pieces and five passage pieces into a
    /// budget of six: three each.
    /// </summary>
    [Fact]
    public void Encode_WhenBothSidesWantMoreThanHalf_SplitsTheBudgetBetweenThem()
    {
        var tokenizer = new CrossEncoderPairTokenizer(_vocabPath);

        // "cells and cells" is cell ##s and cell ##s.
        var (inputIds, _, tokenTypeIds) = tokenizer.Encode("microtubule cells in", "cells and cells", maxLength: 9);

        Assert.Equal(3, QueryLength(tokenTypeIds));
        Assert.Equal(3, PassageLength(inputIds, tokenTypeIds));
        Assert.Equal(9, inputIds.Length);
    }

    /// <summary>
    /// MaxLength is a hard ceiling on the whole feed, whichever side the text is on. The cases run
    /// from "both sides fit" down to "neither does".
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(12)]
    [InlineData(64)]
    public void Encode_NeverProducesMorePositionsThanMaxLength(int maxLength)
    {
        var tokenizer = new CrossEncoderPairTokenizer(_vocabPath);

        var (inputIds, attentionMask, tokenTypeIds) = tokenizer.Encode(
            "microtubule cells in the cell.", "3,520,031 cells and structures of the dog.", maxLength);

        Assert.InRange(inputIds.Length, CrossEncoderPairTokenizer.SpecialTokensPerPair, maxLength);
        Assert.Equal(inputIds.Length, attentionMask.Length);
        Assert.Equal(inputIds.Length, tokenTypeIds.Length);
    }

    /// <summary>Content positions in the query segment: everything between [CLS] and the first [SEP].</summary>
    private static int QueryLength(long[] tokenTypeIds) => FirstSeparatorIndex(tokenTypeIds) - 1;

    /// <summary>Content positions in the passage segment: everything between the two [SEP]s.</summary>
    private static int PassageLength(long[] inputIds, long[] tokenTypeIds) =>
        inputIds.Length - QueryLength(tokenTypeIds) - CrossEncoderPairTokenizer.SpecialTokensPerPair;

    /// <summary>The ids that no vocabulary line resolves to a usable content token.</summary>
    private static List<long> UnresolvedContentTokens(long[] inputIds, long[] tokenTypeIds)
    {
        var firstSeparator = FirstSeparatorIndex(tokenTypeIds);
        var unresolved = new List<long>();
        for (var i = 1; i < inputIds.Length - 1; i++)
        {
            if (i == firstSeparator)
                continue; // the [SEP] between the two segments

            var id = inputIds[i];
            if (id == UnknownId || id < 0 || id >= VocabLines.Length)
            {
                unresolved.Add(id);
            }
        }

        return unresolved;
    }

    /// <summary>
    /// The index of the [SEP] that ends the query segment: the position before the first token the
    /// segment ids assign to the passage. Derived from the feed rather than from the query length,
    /// so it holds whatever the tokenizer produced.
    /// </summary>
    private static int FirstSeparatorIndex(long[] tokenTypeIds)
    {
        for (var i = 0; i < tokenTypeIds.Length; i++)
        {
            if (tokenTypeIds[i] == 1)
                return i - 1;
        }

        return tokenTypeIds.Length - 1;
    }

    /// <summary>Names the ids, so a failure reads as tokens rather than as numbers.</summary>
    private static string[] Decode(long[] inputIds)
    {
        var tokens = new string[inputIds.Length];
        for (var i = 0; i < inputIds.Length; i++)
        {
            var id = inputIds[i];
            tokens[i] = id >= 0 && id < VocabLines.Length
                ? VocabLines[id]
                : $"<id {id} not in vocabulary>";
        }

        return tokens;
    }
}
