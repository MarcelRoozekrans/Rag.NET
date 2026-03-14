using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Search;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Ingestion;

public class DocumentIngestorParentChunkTests
{
    [Fact]
    public async Task IngestAsync_WithParentOptions_StoresParentChunks()
    {
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var texts = ci.Arg<IEnumerable<string>>().ToList();
                var result = new GeneratedEmbeddings<Embedding<float>>();
                foreach (var _ in texts)
                    result.Add(new Embedding<float>(new float[] { 0.1f }));
                return result;
            });

        var parentStore = new InMemoryParentChunkStore();
        var parentOptions = new ParentDocumentOptions { ParentChunkSize = 100, ParentOverlap = 0 };

        var sut = new DocumentIngestor(
            [new Rag.NET.Parsers.TextDocumentParser()],
            new RecursiveChunkingStrategy(),
            vectorStore,
            embedder,
            new ChunkingOptions { MaxChunkSize = 30, Overlap = 0 },
            new InMemoryBm25Index(),
            parentStore,
            parentOptions);

        var text = string.Join(" ", Enumerable.Range(0, 50).Select(i => $"word{i}"));
        using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        var metadata = new DocumentMetadata
        {
            DocumentId = "doc1",
            FileName = "test.txt",
            ContentType = "text/plain"
        };

        await sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        // Parent chunks should be stored
        Assert.True(parentStore.TryGet("doc1", 0, out var parentText));
        Assert.NotNull(parentText);
        Assert.True(parentText.Length > 0);
    }

    [Fact]
    public async Task IngestAsync_WithParentOptions_ChildChunksHaveParentKey()
    {
        var storedChunks = new List<EmbeddedChunk>();
        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.StoreAsync(Arg.Any<IReadOnlyList<EmbeddedChunk>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                storedChunks.AddRange(ci.Arg<IReadOnlyList<EmbeddedChunk>>());
                return Task.CompletedTask;
            });

        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var texts = ci.Arg<IEnumerable<string>>().ToList();
                var result = new GeneratedEmbeddings<Embedding<float>>();
                foreach (var _ in texts)
                    result.Add(new Embedding<float>(new float[] { 0.1f }));
                return result;
            });

        var parentStore = new InMemoryParentChunkStore();
        var parentOptions = new ParentDocumentOptions { ParentChunkSize = 200, ParentOverlap = 0 };

        var sut = new DocumentIngestor(
            [new Rag.NET.Parsers.TextDocumentParser()],
            new RecursiveChunkingStrategy(),
            vectorStore,
            embedder,
            new ChunkingOptions { MaxChunkSize = 30, Overlap = 0 },
            new InMemoryBm25Index(),
            parentStore,
            parentOptions);

        var text = string.Join(" ", Enumerable.Range(0, 50).Select(i => $"word{i}"));
        using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        var metadata = new DocumentMetadata
        {
            DocumentId = "doc1",
            FileName = "test.txt",
            ContentType = "text/plain"
        };

        await sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(storedChunks);
        // Every child chunk should have _parentKey metadata
        Assert.All(storedChunks, ec =>
            Assert.True(ec.Chunk.Metadata.ContainsKey("_parentKey"),
                $"Chunk {ec.Chunk.ChunkIndex} missing _parentKey"));
    }

    [Fact]
    public async Task IngestAsync_WithoutParentOptions_NoParentKeyMetadata()
    {
        var storedChunks = new List<EmbeddedChunk>();
        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.StoreAsync(Arg.Any<IReadOnlyList<EmbeddedChunk>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                storedChunks.AddRange(ci.Arg<IReadOnlyList<EmbeddedChunk>>());
                return Task.CompletedTask;
            });

        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var texts = ci.Arg<IEnumerable<string>>().ToList();
                var result = new GeneratedEmbeddings<Embedding<float>>();
                foreach (var _ in texts)
                    result.Add(new Embedding<float>(new float[] { 0.1f }));
                return result;
            });

        // No parentStore, no parentOptions
        var sut = new DocumentIngestor(
            [new Rag.NET.Parsers.TextDocumentParser()],
            new RecursiveChunkingStrategy(),
            vectorStore,
            embedder,
            new ChunkingOptions { MaxChunkSize = 30, Overlap = 0 },
            new InMemoryBm25Index());

        var text = "Hello world this is a test document with some words.";
        using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        var metadata = new DocumentMetadata
        {
            DocumentId = "doc1",
            FileName = "test.txt",
            ContentType = "text/plain"
        };

        await sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        // No child should have _parentKey
        Assert.All(storedChunks, ec =>
            Assert.False(ec.Chunk.Metadata.ContainsKey("_parentKey")));
    }

    [Fact]
    public async Task DeleteAsync_WithParentOptions_RemovesFromParentStore()
    {
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var parentStore = new InMemoryParentChunkStore();
        parentStore.Add("doc1", 0, "parent text");

        var sut = new DocumentIngestor(
            [new Rag.NET.Parsers.TextDocumentParser()],
            new RecursiveChunkingStrategy(),
            vectorStore,
            embedder,
            new ChunkingOptions(),
            new InMemoryBm25Index(),
            parentStore,
            new ParentDocumentOptions());

        await sut.DeleteAsync("doc1", TestContext.Current.CancellationToken);

        Assert.False(parentStore.TryGet("doc1", 0, out _));
    }
}
