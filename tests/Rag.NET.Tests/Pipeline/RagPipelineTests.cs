using System.Text;
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using Xunit;

namespace Rag.NET.Tests.Pipeline;

public class RagPipelineTests
{
    private readonly IDocumentParser _parser = Substitute.For<IDocumentParser>();
    private readonly IChunkingStrategy _chunker = Substitute.For<IChunkingStrategy>();
    private readonly IVectorStore _vectorStore = Substitute.For<IVectorStore>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly RagPipeline _sut;

    public RagPipelineTests()
    {
        _parser.CanParse(Arg.Any<string>()).Returns(true);
        _sut = new RagPipeline(
            [_parser],
            _chunker,
            _vectorStore,
            _embedder,
            chatClient: null,
            new ChunkingOptions());
    }

    [Fact]
    public async Task IngestAsync_OrchestratesParseChunkEmbedStore()
    {
        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
        var section = new DocumentSection { Text = "Hello world", DocumentId = "doc-1", SectionIndex = 0 };
        var chunk = new TextChunk { Text = "Hello world", DocumentId = "doc-1", ChunkIndex = 0 };
        var embedding = new Embedding<float>(new float[] { 0.1f, 0.2f, 0.3f });

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(section));

        _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk));

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello world"));

        var result = await _sut.IngestAsync(stream, metadata, TestContext.Current.CancellationToken);

        Assert.Equal("doc-1", result.DocumentId);
        Assert.Equal(1, result.ChunksStored);
        await _vectorStore.Received(1).StoreAsync(
            Arg.Is<IReadOnlyList<EmbeddedChunk>>(c => c.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_EmbedsQueryAndSearches()
    {
        var queryEmbedding = new Embedding<float>(new float[] { 0.1f, 0.2f });
        var searchResult = new SearchResult
        {
            Chunk = new TextChunk { Text = "result", DocumentId = "doc-1", ChunkIndex = 0 },
            Score = 0.95
        };

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns([searchResult]);

        var results = await _sut.RetrieveAsync("test query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal(0.95, results[0].Score);
    }

    [Fact]
    public async Task AskAsync_WithoutChatClient_ThrowsInvalidOperation()
    {
        var queryEmbedding = new Embedding<float>(new float[] { 0.1f });
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.AskAsync("question", cancellationToken: TestContext.Current.CancellationToken));
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(params T[] items)
    {
        foreach (var item in items)
        {
            yield return item;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
