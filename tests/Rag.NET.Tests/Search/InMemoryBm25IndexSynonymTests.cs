using Rag.NET.Models;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.Search;

public class InMemoryBm25IndexSynonymTests
{
    private static TextChunk Chunk(string text, string docId = "doc-1", int chunkIndex = 0) =>
        new() { Text = text, DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex };

    [Fact]
    public void Search_IndexKubernetes_QueryK8s_HitsReturned()
    {
        var synonyms = new SynonymMap([["k8s", "kubernetes"]]);
        var index = new InMemoryBm25Index(synonyms);
        index.Add(0, Chunk("deploying kubernetes clusters"));

        var results = index.Search("k8s", topK: 5);

        Assert.Single(results);
    }

    [Fact]
    public void Search_IndexK8s_QueryKubernetes_HitsReturned()
    {
        var synonyms = new SynonymMap([["k8s", "kubernetes"]]);
        var index = new InMemoryBm25Index(synonyms);
        index.Add(0, Chunk("k8s deployment guide"));

        var results = index.Search("kubernetes", topK: 5);

        Assert.Single(results);
    }

    [Fact]
    public void Search_NoSynonymMap_ExistingBehaviourUnchanged()
    {
        var index = new InMemoryBm25Index();
        index.Add(0, Chunk("kubernetes cluster"));

        // No synonyms — "k8s" should NOT match
        var results = index.Search("k8s", topK: 5);

        Assert.Empty(results);
    }

    [Fact]
    public void Search_ThreeTermSynonymGroup_AllFormsMatch()
    {
        var synonyms = new SynonymMap([["MI", "myocardial infarction", "heart attack"]]);
        var index = new InMemoryBm25Index(synonyms);
        index.Add(0, Chunk("patient had a myocardial infarction"));

        Assert.NotEmpty(index.Search("MI", topK: 5));
        Assert.NotEmpty(index.Search("heart attack", topK: 5));
        Assert.NotEmpty(index.Search("myocardial infarction", topK: 5));
    }

    [Fact]
    public void Search_SynonymAddedAtRuntime_NewQueryMatches()
    {
        var synonyms = new SynonymMap();
        var index = new InMemoryBm25Index(synonyms);
        index.Add(0, Chunk("javascript framework"));

        // Before synonym
        Assert.Empty(index.Search("js", topK: 5));

        // Add synonym at runtime
        synonyms.AddGroup("js", "javascript");

        // After synonym — index was already built, but query expansion now applies
        Assert.NotEmpty(index.Search("js", topK: 5));
    }
}
