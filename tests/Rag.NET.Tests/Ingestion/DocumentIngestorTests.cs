using System.Text;
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.Ingestion;

public class DocumentIngestorTests
{
    private readonly IDocumentParser _parser = Substitute.For<IDocumentParser>();
    private readonly IChunkingStrategy _chunker = Substitute.For<IChunkingStrategy>();
    private readonly IVectorStore _vectorStore = Substitute.For<IVectorStore>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly InMemoryBm25Index _bm25Index = new();
    private readonly DocumentIngestor _sut;

    public DocumentIngestorTests()
    {
        _parser.CanParse(Arg.Any<string>()).Returns(true);
        _sut = new DocumentIngestor([_parser], _chunker, _vectorStore, _embedder, new ChunkingOptions(), _bm25Index);
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
        var result = await _sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("doc-1", result.DocumentId);
        Assert.Equal(1, result.ChunksStored);
        await _vectorStore.Received(1).StoreAsync(
            Arg.Is<IReadOnlyList<EmbeddedChunk>>(c => c.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_EmptyDocument_ReturnsZeroChunks()
    {
        var metadata = new DocumentMetadata { DocumentId = "doc-2", FileName = "empty.txt", ContentType = "text/plain" };

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<DocumentSection>());

        using var stream = new MemoryStream();
        var result = await _sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ChunksStored);
        await _vectorStore.DidNotReceive().StoreAsync(Arg.Any<IReadOnlyList<EmbeddedChunk>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_DelegatesToVectorStoreAndBm25()
    {
        await _sut.DeleteAsync("doc-1", TestContext.Current.CancellationToken);

        await _vectorStore.Received(1).DeleteByDocumentIdAsync("doc-1", Arg.Any<CancellationToken>());
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(params T[] items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.CompletedTask;
        }
    }
}
