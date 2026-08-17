using Rag.NET.Models;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.Search;

/// <summary>
/// Covers BM25 over scripts that do not put spaces between words.
/// </summary>
/// <remarks>
/// <para>
/// <b>#299.</b> The tokeniser split on runs of <see cref="char.IsLetterOrDigit(char)"/>, which is
/// Unicode-aware and correct for every script that delimits words. Chinese, Japanese and Korean do
/// not, so a whole sentence became one token — term frequency became meaningless and hybrid
/// retrieval silently degraded to dense-only for those languages, without erroring.
/// </para>
/// <para>
/// Overlapping character bigrams now cover those ranges, which is what Lucene's CJK analyzer does:
/// no dictionary, no segmentation model, one extra term per character.
/// </para>
/// </remarks>
public sealed class CjkTokenisationTests
{
    [Theory]
    [InlineData("人工智能是未来", "Chinese")]
    [InlineData("東京は日本の首都です", "Japanese")]
    [InlineData("서울은 대한민국의 수도입니다", "Korean")]
    public void ACjkSentenceIsMoreThanOneToken(string sentence, string language)
    {
        var tokens = InMemoryBm25Index.Tokenize(sentence);

        // The defect exactly: one token for an entire sentence.
        Assert.True(
            tokens.Count > 1,
            $"{language}: {sentence} produced {tokens.Count} token(s). One token per sentence makes " +
            "term frequency meaningless and BM25 matches nothing short of an exact sentence repeat.");
    }

    [Theory]
    [InlineData("the quick brown fox", 4)]
    [InlineData("Ångström och Kelvin", 3)]
    [InlineData("Москва это столица", 3)]
    [InlineData("الذكاء الاصطناعي", 2)]
    public void TextWithoutCjkTokenisesExactlyAsBefore(string text, int expected)
    {
        // The reason this is a default rather than an option: the CJK ranges appear in no Latin,
        // Cyrillic, Greek, Arabic or Hebrew text, so every existing corpus is unaffected.
        Assert.Equal(expected, InMemoryBm25Index.Tokenize(text).Count);
    }

    [Fact]
    public void CjkAndLatinInOneSentenceKeepTheirOwnRules()
    {
        var tokens = InMemoryBm25Index.Tokenize("Rag.NET 人工智能 v1.0");

        // Latin and digits still split on word boundaries...
        Assert.Contains("rag", tokens, StringComparer.Ordinal);
        Assert.Contains("net", tokens, StringComparer.Ordinal);

        // ...and the CJK run contributes bigrams beside them, rather than swallowing the line.
        Assert.Contains("人工", tokens, StringComparer.Ordinal);
        Assert.Contains("工智", tokens, StringComparer.Ordinal);
        Assert.Contains("智能", tokens, StringComparer.Ordinal);
    }

    [Fact]
    public void ASingleCjkCharacterIsStillSearchable()
    {
        // The edge case a naive bigram loop drops: a one-character run yields itself, not nothing.
        Assert.Contains("水", InMemoryBm25Index.Tokenize("水"), StringComparer.Ordinal);
    }

    /// <remarks>
    /// The point of the whole change, at the level a caller sees: a Chinese query retrieving the
    /// Chinese document. Before, every document tokenised to one sentence-long term and only an
    /// exact repeat could match.
    /// </remarks>
    [Fact]
    public void AChineseQueryRetrievesTheChineseDocument()
    {
        using var index = new InMemoryBm25Index();
        index.Add(1, Chunk("doc-cn", "人工智能是未来的核心技术"));
        index.Add(2, Chunk("doc-other", "今天天气很好我们去公园散步"));

        var hits = index.Search("人工智能", 5);

        Assert.NotEmpty(hits);
        Assert.Equal("doc-cn", hits[0].chunk.DocumentId.Value);
    }

    private static TextChunk Chunk(string documentId, string text) => new()
    {
        Text = text,
        DocumentId = new DocumentId(documentId),
        ChunkIndex = 0,
    };
}
