using Rag.NET.Abstractions;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.Search;

public class InMemoryTagIndexTests
{
    private static ReadOnlyMemory<float> Vec(params float[] v) => v;

    [Fact]
    public void Search_ReturnsMatchesAboveMinScore()
    {
        var index = new InMemoryTagIndex();
        index.Add("dept", "finance", Vec(1f, 0f));
        index.Add("dept", "legal",   Vec(0f, 1f));

        // Query close to "finance" (1,0)
        var results = index.Search(Vec(0.99f, 0.01f), minScore: 0.9);

        Assert.Single(results);
        Assert.Equal("finance", results[0].Value);
        Assert.Equal("dept",    results[0].Key);
    }

    [Fact]
    public void Search_OrderedByScoreDescending()
    {
        var index = new InMemoryTagIndex();
        index.Add("dept", "finance",   Vec(1f, 0f, 0f));
        index.Add("dept", "marketing", Vec(0.9f, 0.1f, 0f));

        var results = index.Search(Vec(1f, 0f, 0f), minScore: 0.0);

        Assert.True(results[0].Score >= results[1].Score);
    }

    [Fact]
    public void Add_Duplicate_SecondIsIgnored()
    {
        var index = new InMemoryTagIndex();
        index.Add("dept", "finance", Vec(1f, 0f));
        index.Add("dept", "finance", Vec(0f, 1f)); // different embedding — ignored

        // Search with vector (1,0) — only first embedding matters
        var results = index.Search(Vec(1f, 0f), minScore: 0.9);
        Assert.Single(results);
    }

    [Fact]
    public void Contains_ReturnsTrueAfterAdd()
    {
        var index = new InMemoryTagIndex();
        Assert.False(index.Contains("dept", "finance"));
        index.Add("dept", "finance", Vec(1f, 0f));
        Assert.True(index.Contains("dept", "finance"));
    }

    [Fact]
    public void Search_EmptyIndex_ReturnsEmpty()
    {
        var index = new InMemoryTagIndex();
        var results = index.Search(Vec(1f, 0f), minScore: 0.5);
        Assert.Empty(results);
    }
}
