using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Serialization;

public class MetadataSerializerTests
{
    [Fact]
    public void DeserializeMetadata_ValidJson_ReturnsDict()
    {
        var json = """{"key1":"value1","key2":"value2"}""";

        var result = MetadataSerializer.DeserializeMetadata(json);

        Assert.True(result.IsSuccess);
        Assert.Equal("value1", result.Value["key1"]);
        Assert.Equal("value2", result.Value["key2"]);
    }

    [Fact]
    public void DeserializeMetadata_NullInput_ReturnsEmptyDict()
    {
        var result = MetadataSerializer.DeserializeMetadata(null);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact]
    public void DeserializeMetadata_EmptyString_ReturnsEmptyDict()
    {
        var result = MetadataSerializer.DeserializeMetadata(string.Empty);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact]
    public void DeserializeMetadata_MalformedJson_ReturnsError()
    {
        var result = MetadataSerializer.DeserializeMetadata("not json {{{");

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void SerializeMetadata_Roundtrip_PreservesData()
    {
        var dict = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["key1"] = "value1" };

        var json = MetadataSerializer.SerializeMetadata(dict);
        var roundtrip = MetadataSerializer.DeserializeMetadata(json);

        Assert.True(roundtrip.IsSuccess);
        Assert.Equal<MetadataValue>("value1", roundtrip.Value["key1"]);
    }

    [Fact]
    public void SerializeMetadata_TypedRoundtrip_PreservesEveryKind()
    {
        // The JSON round-trip every blob-persisting store shares (Qdrant payload, Weaviate
        // metadata_json, PgVector jsonb, SQLite, the Azure legacy field): each kind must come
        // back as itself — a number returning as "3" is the flattening bug this design removes.
        var reviewedAt = new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);
        var dict = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["page"] = 3,
            ["rating"] = 4.5,
            ["published"] = true,
            ["reviewed_at"] = reviewedAt,
            ["source"] = "unit",
        };

        var roundtrip = MetadataSerializer.DeserializeMetadata(MetadataSerializer.SerializeMetadata(dict));

        Assert.True(roundtrip.IsSuccess);
        Assert.Equal(MetadataValueKind.Number, roundtrip.Value["page"].Kind);
        Assert.Equal(3d, roundtrip.Value["page"].NumberValue);
        Assert.Equal(4.5, roundtrip.Value["rating"].NumberValue);
        Assert.True(roundtrip.Value["published"].BooleanValue);
        Assert.Equal(MetadataValueKind.DateTimeOffset, roundtrip.Value["reviewed_at"].Kind);
        Assert.Equal(reviewedAt, roundtrip.Value["reviewed_at"].DateTimeOffsetValue);
        Assert.Equal(MetadataValueKind.String, roundtrip.Value["source"].Kind);
    }

    [Fact]
    public void DeserializeMetadata_LegacyStringOnlyJson_ReadsBackAsStringKind()
    {
        // Metadata written before values carried types is JSON with string values only —
        // including strings that merely look like numbers or ISO dates. The backward-compat
        // contract: nothing throws, nothing is lost, and nothing gets guessed into another
        // kind — "3" stays a String, it does not become the Number 3.
        var legacy = """{"page":"3","source":"crm","when":"2026-05-04T12:00:00.0000000Z"}""";

        var result = MetadataSerializer.DeserializeMetadata(legacy);

        Assert.True(result.IsSuccess);
        Assert.Equal(MetadataValueKind.String, result.Value["page"].Kind);
        Assert.Equal("3", result.Value["page"].StringValue);
        Assert.Equal(MetadataValueKind.String, result.Value["when"].Kind);
        Assert.Equal(MetadataValueKind.String, result.Value["source"].Kind);
    }

    [Fact]
    public void SerializeMetadata_DateAndLookalikeString_StayDistinguishable()
    {
        // Dates serialize wrapped ({"$date": ...}) precisely so that a real date and a string
        // that happens to hold the same ISO text do not collapse into one thing on the trip.
        var instant = new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);
        var dict = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["real_date"] = instant,
            ["date_text"] = ((MetadataValue)instant).ToString(),
        };

        var roundtrip = MetadataSerializer.DeserializeMetadata(MetadataSerializer.SerializeMetadata(dict));

        Assert.True(roundtrip.IsSuccess);
        Assert.Equal(MetadataValueKind.DateTimeOffset, roundtrip.Value["real_date"].Kind);
        Assert.Equal(MetadataValueKind.String, roundtrip.Value["date_text"].Kind);
        Assert.NotEqual(roundtrip.Value["real_date"], roundtrip.Value["date_text"]);
    }
}
