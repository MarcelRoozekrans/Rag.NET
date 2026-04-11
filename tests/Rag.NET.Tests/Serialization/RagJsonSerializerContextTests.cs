using System.Text.Json;
using Xunit;

namespace Rag.NET.Tests.Serialization;

public class RagJsonSerializerContextTests
{
    [Fact]
    public void DictionaryStringString_Roundtrip_PreservesData()
    {
        var original = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["key1"] = "value1",
            ["key2"] = "value2",
        };

        var json = JsonSerializer.Serialize(original, RagJsonSerializerContext.Default.DictionaryStringString);
        var deserialized = JsonSerializer.Deserialize(json, RagJsonSerializerContext.Default.DictionaryStringString);

        Assert.NotNull(deserialized);
        Assert.Equal(original, deserialized);
    }

    [Fact]
    public void ListString_Roundtrip_PreservesData()
    {
        var original = new List<string> { "claim 1", "claim 2", "claim 3" };

        var json = JsonSerializer.Serialize(original, RagJsonSerializerContext.Default.ListString);
        var deserialized = JsonSerializer.Deserialize(json, RagJsonSerializerContext.Default.ListString);

        Assert.NotNull(deserialized);
        Assert.Equal(original, deserialized);
    }

    [Fact]
    public void DictionaryStringString_DeserializeNull_ReturnsNull()
    {
        var result = JsonSerializer.Deserialize("null", RagJsonSerializerContext.Default.DictionaryStringString);

        Assert.Null(result);
    }
}
