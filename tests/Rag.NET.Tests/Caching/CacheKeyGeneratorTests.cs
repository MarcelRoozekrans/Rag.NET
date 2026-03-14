using Rag.NET.Caching;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Caching;

public class CacheKeyGeneratorTests
{
    [Fact]
    public void ForEmbedding_SameText_ReturnsSameKey()
    {
        var key1 = CacheKeyGenerator.ForEmbedding("hello world");
        var key2 = CacheKeyGenerator.ForEmbedding("hello world");
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void ForEmbedding_DifferentText_ReturnsDifferentKey()
    {
        var key1 = CacheKeyGenerator.ForEmbedding("hello");
        var key2 = CacheKeyGenerator.ForEmbedding("world");
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void ForEmbedding_HasPrefix()
    {
        var key = CacheKeyGenerator.ForEmbedding("test");
        Assert.StartsWith("rag:embed:", key, StringComparison.Ordinal);
    }

    [Fact]
    public void ForResult_SameOptions_ReturnsSameKey()
    {
        var opts = new RetrievalOptions { TopK = 10 };
        var key1 = CacheKeyGenerator.ForResult("query", opts);
        var key2 = CacheKeyGenerator.ForResult("query", opts);
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void ForResult_DifferentTopK_ReturnsDifferentKey()
    {
        var key1 = CacheKeyGenerator.ForResult("query", new RetrievalOptions { TopK = 5 });
        var key2 = CacheKeyGenerator.ForResult("query", new RetrievalOptions { TopK = 10 });
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void ForResult_DifferentQuery_ReturnsDifferentKey()
    {
        var opts = new RetrievalOptions();
        var key1 = CacheKeyGenerator.ForResult("query1", opts);
        var key2 = CacheKeyGenerator.ForResult("query2", opts);
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void ForResult_HasPrefix()
    {
        var key = CacheKeyGenerator.ForResult("test", new RetrievalOptions());
        Assert.StartsWith("rag:result:", key, StringComparison.Ordinal);
    }

    [Fact]
    public void ForResult_MetadataFilterAffectsKey()
    {
        var key1 = CacheKeyGenerator.ForResult("q", new RetrievalOptions());
        var key2 = CacheKeyGenerator.ForResult("q", new RetrievalOptions
        {
            MetadataFilter = new Dictionary<string, string>(StringComparer.Ordinal) { ["dept"] = "eng" }
        });
        Assert.NotEqual(key1, key2);
    }
}
