using System.Text;
using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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

    [Fact]
    public async Task IngestAsync_NoParserRegistered_ThrowsInvalidOperationException()
    {
        var noParserIngestor = new DocumentIngestor(
            [], _chunker, _vectorStore, _embedder, new ChunkingOptions(), _bm25Index);

        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("test"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => noParserIngestor.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IngestAsync_ParserCannotParseContentType_ThrowsInvalidOperationException()
    {
        _parser.CanParse("application/pdf").Returns(false);

        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.pdf", ContentType = "application/pdf" };

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("pdf content"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IngestAsync_EmbedderFails_PropagatesException()
    {
        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
        var section = new DocumentSection { Text = "Hello", DocumentId = "doc-1", SectionIndex = 0 };
        var chunk = new TextChunk { Text = "Hello", DocumentId = "doc-1", ChunkIndex = 0 };

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(section));
        _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk));
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Embedding API unreachable"));

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello"));
        await Assert.ThrowsAsync<HttpRequestException>(
            () => _sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IngestAsync_WithOverwrite_DeletesBeforeIngesting()
    {
        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<DocumentSection>());

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("test"));
        await _sut.IngestAsync(stream, metadata, new IngestionOptions { Overwrite = true },
            cancellationToken: TestContext.Current.CancellationToken);

        await _vectorStore.Received(1).DeleteByDocumentIdAsync("doc-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_ReportsProgress()
    {
        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
        var section = new DocumentSection { Text = "Hello", DocumentId = "doc-1", SectionIndex = 0 };
        var chunk = new TextChunk { Text = "Hello", DocumentId = "doc-1", ChunkIndex = 0 };
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(section));
        _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk));
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        var reports = new List<IngestionProgress>();
        var progress = Substitute.For<IProgress<IngestionProgress>>();
        progress.When(p => p.Report(Arg.Any<IngestionProgress>()))
            .Do(ci => reports.Add(ci.Arg<IngestionProgress>()));

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello"));
        await _sut.IngestAsync(stream, metadata, progress: progress,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(reports.Count >= 3);
    }

    [Fact]
    public async Task IngestAsync_NonSeekableStream_WithParentOptions_ThrowsBeforeParsingStarts()
    {
        // Arrange: non-seekable stream
        var stream = new NonSeekableStream(new MemoryStream("hello world"u8.ToArray()));
        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt" };

        var parentStore = Substitute.For<IParentChunkStore>();
        var sut = new DocumentIngestor(
            [_parser], _chunker, _vectorStore, _embedder,
            new ChunkingOptions(), _bm25Index,
            parentStore: parentStore,
            parentOptions: new ParentDocumentOptions());

        // Act & Assert: must throw BEFORE calling ParseAsync
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken));

        _ = _parser.DidNotReceive().ParseAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>());
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(params T[] items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.CompletedTask;
        }
    }

    // Helper: minimal non-seekable stream wrapper
    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
