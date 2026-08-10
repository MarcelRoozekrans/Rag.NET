using Rag.NET.Caching;
using Rag.NET.Models;
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
        Assert.NotEqual(key1, key2, StringComparer.Ordinal);
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
        Assert.NotEqual(key1, key2, StringComparer.Ordinal);
    }

    [Fact]
    public void ForResult_DifferentQuery_ReturnsDifferentKey()
    {
        var opts = new RetrievalOptions();
        var key1 = CacheKeyGenerator.ForResult("query1", opts);
        var key2 = CacheKeyGenerator.ForResult("query2", opts);
        Assert.NotEqual(key1, key2, StringComparer.Ordinal);
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
            MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["dept"] = "eng" }
        });
        Assert.NotEqual(key1, key2, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(0.0, 0.5)]
    public void ForResult_DifferentMinScore_ReturnsDifferentKey(double score1, double score2)
    {
        var key1 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { MinScore = score1 });
        var key2 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { MinScore = score2 });
        Assert.NotEqual(key1, key2, StringComparer.Ordinal);
    }

    [Fact]
    public void ForResult_DifferentUseHybridSearch_ReturnsDifferentKey()
    {
        var key1 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { UseHybridSearch = false });
        var key2 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { UseHybridSearch = true });
        Assert.NotEqual(key1, key2, StringComparer.Ordinal);
    }

    [Fact]
    public void ForResult_DifferentUseRedundancyFilter_ReturnsDifferentKey()
    {
        var key1 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { UseRedundancyFilter = false });
        var key2 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { UseRedundancyFilter = true });
        Assert.NotEqual(key1, key2, StringComparer.Ordinal);
    }

    [Fact]
    public void ForResult_DifferentUseMultiQuery_ReturnsDifferentKey()
    {
        var key1 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { UseMultiQuery = false });
        var key2 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { UseMultiQuery = true });
        Assert.NotEqual(key1, key2, StringComparer.Ordinal);
    }

    [Fact]
    public void ForResult_DifferentUseReranking_ReturnsDifferentKey()
    {
        var key1 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { UseReranking = false });
        var key2 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { UseReranking = true });
        Assert.NotEqual(key1, key2, StringComparer.Ordinal);
    }

    [Fact]
    public void ForResult_DifferentUseHyde_ReturnsDifferentKey()
    {
        var key1 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { UseHyde = false });
        var key2 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { UseHyde = true });
        Assert.NotEqual(key1, key2, StringComparer.Ordinal);
    }

    [Fact]
    public void ForResult_DifferentCandidateCount_ReturnsDifferentKey()
    {
        var key1 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { CandidateCount = 10 });
        var key2 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { CandidateCount = 20 });
        Assert.NotEqual(key1, key2, StringComparer.Ordinal);
    }

    [Fact]
    public void ForResult_DifferentRedundancyThreshold_ReturnsDifferentKey()
    {
        var key1 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { RedundancyThreshold = 0.9f });
        var key2 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { RedundancyThreshold = 0.8f });
        Assert.NotEqual(key1, key2, StringComparer.Ordinal);
    }

    [Fact]
    public void ForResult_DifferentUseLostInTheMiddle_ReturnsDifferentKey()
    {
        var key1 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { UseLostInTheMiddleReordering = false });
        var key2 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { UseLostInTheMiddleReordering = true });
        Assert.NotEqual(key1, key2, StringComparer.Ordinal);
    }

    [Fact]
    public void ForResult_DifferentUseParentDocument_ReturnsDifferentKey()
    {
        var key1 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { UseParentDocument = false });
        var key2 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { UseParentDocument = true });
        Assert.NotEqual(key1, key2, StringComparer.Ordinal);
    }

    [Fact]
    public void ForResult_MetadataFilterOrderDoesNotAffectKey()
    {
        var filter1 = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["a"] = "1", ["b"] = "2" };
        var filter2 = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["b"] = "2", ["a"] = "1" };
        var key1 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { MetadataFilter = filter1 });
        var key2 = CacheKeyGenerator.ForResult("q", new RetrievalOptions { MetadataFilter = filter2 });
        Assert.Equal(key1, key2);
    }
}
