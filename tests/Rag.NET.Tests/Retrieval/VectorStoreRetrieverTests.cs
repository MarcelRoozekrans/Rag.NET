using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

public class VectorStoreRetrieverTests
{
    private readonly IVectorStore _vectorStore = Substitute.For<IVectorStore>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly InMemoryBm25Index _bm25Index = new();
    private readonly VectorStoreRetriever _sut;

    public VectorStoreRetrieverTests()
    {
        _sut = new VectorStoreRetriever(_vectorStore, _embedder, _bm25Index);
    }

    [Fact]
    public async Task RetrieveAsync_EmbedsQueryAndSearchesVectorStore()
    {
        var queryEmbedding = new Embedding<float>(new float[] { 0.1f, 0.2f });
        var expected = new SearchResult
        {
            Chunk = new TextChunk { Text = "result", DocumentId = "doc-1", ChunkIndex = 0 },
            Score = 0.95
        };

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns([expected]);

        var results = await _sut.RetrieveAsync("test query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal(0.95, results[0].Score);
    }

    [Fact]
    public async Task RetrieveAsync_UseHybridSearch_WithHybridSearchable_CallsHybridSearchAsync()
    {
        var hybridStore = Substitute.For<IVectorStore, IHybridSearchable>();
        var sut = new VectorStoreRetriever(hybridStore, _embedder, _bm25Index);

        var queryEmbedding = new Embedding<float>(new float[] { 0.1f, 0.2f });
        var expected = new SearchResult
        {
            Chunk = new TextChunk { Text = "hybrid", DocumentId = "doc-1", ChunkIndex = 0 },
            Score = 0.9
        };

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));

        ((IHybridSearchable)hybridStore).HybridSearchAsync(
            Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns([expected]);

        var results = await sut.RetrieveAsync("test", new RetrievalOptions { UseHybridSearch = true }, TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("hybrid", results[0].Chunk.Text);
    }

    [Fact]
    public async Task RetrieveAsync_UseHybridSearch_WithoutHybridSearchable_UsesBm25Fallback()
    {
        _bm25Index.Add(1, new TextChunk { Text = "hello world bm25", DocumentId = "doc-1", ChunkIndex = 0 });

        var queryEmbedding = new Embedding<float>(new float[] { 0.1f, 0.2f });
        var denseResult = new SearchResult
        {
            Chunk = new TextChunk { Text = "dense result", DocumentId = "doc-2", ChunkIndex = 0 },
            Score = 0.8
        };

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns([denseResult]);

        var results = await _sut.RetrieveAsync("hello world", new RetrievalOptions { UseHybridSearch = true }, TestContext.Current.CancellationToken);

        Assert.NotEmpty(results);
    }
}
