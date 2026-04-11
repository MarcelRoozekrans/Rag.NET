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
        var dict = new Dictionary<string, string>(StringComparer.Ordinal) { ["key1"] = "value1" };

        var json = MetadataSerializer.SerializeMetadata(dict);
        var roundtrip = MetadataSerializer.DeserializeMetadata(json);

        Assert.True(roundtrip.IsSuccess);
        Assert.Equal("value1", roundtrip.Value["key1"]);
    }
}
