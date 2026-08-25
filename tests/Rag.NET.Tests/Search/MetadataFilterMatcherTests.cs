using Rag.NET.Abstractions;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Search;

public sealed class MetadataFilterMatcherTests
{
    private static TextChunk Chunk(params (string Key, MetadataValue Value)[] metadata)
    {
        var dict = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
            dict[key] = value;

        return new TextChunk
        {
            DocumentId = new DocumentId("doc-1"),
            ChunkIndex = 0,
            Text = "text",
            Metadata = dict,
        };
    }

    [Fact]
    public void Matches_NullFilter_MatchesEverything() =>
        Assert.True(MetadataFilterMatcher.Matches(Chunk(("tenant", "a")), null));

    [Fact]
    public void Matches_EmptyFilter_MatchesEverything() =>
        Assert.True(MetadataFilterMatcher.Matches(
            Chunk(("tenant", "a")), new Dictionary<string, MetadataValue>(StringComparer.Ordinal)));

    [Fact]
    public void Matches_MissingKey_DoesNotMatch() =>
        Assert.False(MetadataFilterMatcher.Matches(
            Chunk(("tenant", "a")),
            new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["absent"] = "a" }));

    [Fact]
    public void Matches_DifferentValue_DoesNotMatch() =>
        Assert.False(MetadataFilterMatcher.Matches(
            Chunk(("tenant", "a")),
            new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["tenant"] = "b" }));

    [Fact]
    public void Matches_EveryPairMustMatch_AndSemantics() =>
        Assert.False(MetadataFilterMatcher.Matches(
            Chunk(("tenant", "a"), ("lang", "en")),
            new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["tenant"] = "a",
                ["lang"] = "fr",
            }));

    [Fact]
    public void Matches_AllPairsMatch_Matches() =>
        Assert.True(MetadataFilterMatcher.Matches(
            Chunk(("tenant", "a"), ("lang", "en")),
            new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["tenant"] = "a",
                ["lang"] = "en",
            }));

    // The typed-equality guarantee RetrievalOptions.MetadataFilter documents: a Number filter
    // does not match the String form of the same digits. Asserted in both directions because a
    // matcher that coerced one way and not the other would pass a one-directional test.
    [Fact]
    public void Matches_NumberFilterAgainstStringValue_DoesNotMatch() =>
        Assert.False(MetadataFilterMatcher.Matches(
            Chunk(("page", "3")),
            new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["page"] = 3 }));

    [Fact]
    public void Matches_StringFilterAgainstNumberValue_DoesNotMatch() =>
        Assert.False(MetadataFilterMatcher.Matches(
            Chunk(("page", 3)),
            new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["page"] = "3" }));

    // Case-sensitive: "A" does not match "a".
    [Fact]
    public void Matches_StringComparisonIsCaseSensitive() =>
        Assert.False(MetadataFilterMatcher.Matches(
            Chunk(("tenant", "A")),
            new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["tenant"] = "a" }));

    // Also not culture-sensitive: the Turkish "İ"/"i" pair, which a culture-aware comparison
    // (Turkish "i" rules) would fold together, must not match under ordinal comparison.
    [Fact]
    public void Matches_StringComparisonIsNotCultureSensitive() =>
        Assert.False(MetadataFilterMatcher.Matches(
            Chunk(("tenant", "İ")), // İ (Turkish dotted capital I)
            new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["tenant"] = "i" }));
}
