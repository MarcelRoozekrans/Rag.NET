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

    [Fact]
    public async Task AskStreamingAsync_WithoutChatClient_ThrowsInvalidOperation()
    {
        var queryEmbedding = new Embedding<float>(new float[] { 0.1f });
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in _sut.AskStreamingAsync("question", cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        });
    }

    [Fact]
    public async Task AskStreamingAsync_YieldsSourcesFirst_ThenTextDeltas()
    {
        var chatClient = Substitute.For<IChatClient>();
        var sut = new RagPipeline(
            [_parser],
            _chunker,
            _vectorStore,
            _embedder,
            chatClient,
            new ChunkingOptions());

        var queryEmbedding = new Embedding<float>(new float[] { 0.1f });
        var searchResult = new SearchResult
        {
            Chunk = new TextChunk { Text = "relevant context", DocumentId = "doc-1", ChunkIndex = 0 },
            Score = 0.9
        };

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { searchResult });

        chatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(
                new ChatResponseUpdate { Contents = [new TextContent("Hello")] },
                new ChatResponseUpdate { Contents = [new TextContent(" World")] }));

        var updates = new List<RagStreamingUpdate>();
        await foreach (var update in sut.AskStreamingAsync("test question", cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        // First update has sources, no text
        Assert.NotNull(updates[0].Sources);
        Assert.Single(updates[0].Sources!);
        Assert.Null(updates[0].TextDelta);

        // Subsequent updates have text, no sources
        Assert.Equal("Hello", updates[1].TextDelta);
        Assert.Null(updates[1].Sources);
        Assert.Equal(" World", updates[2].TextDelta);
        Assert.Null(updates[2].Sources);

        Assert.Equal(3, updates.Count);
    }

    [Fact]
    public async Task IngestAsync_PropagatesMetadataTagsToChunks()
    {
        var metadata = new DocumentMetadata
        {
            DocumentId = "doc-1",
            FileName = "test.txt",
            ContentType = "text/plain",
            Tags = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["department"] = "engineering",
                ["year"] = "2026",
            },
        };

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
        await _sut.IngestAsync(stream, metadata, TestContext.Current.CancellationToken);

        await _vectorStore.Received(1).StoreAsync(
            Arg.Is<IReadOnlyList<EmbeddedChunk>>(chunks =>
                chunks[0].Chunk.Metadata.ContainsKey("department") &&
                chunks[0].Chunk.Metadata["department"] == "engineering" &&
                chunks[0].Chunk.Metadata.ContainsKey("document_id") &&
                chunks[0].Chunk.Metadata["document_id"] == "doc-1" &&
                chunks[0].Chunk.Metadata.ContainsKey("file_name") &&
                chunks[0].Chunk.Metadata["file_name"] == "test.txt"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_WithHybridSearch_CallsHybridSearchable()
    {
        var hybridStore = Substitute.For<IVectorStore, IHybridSearchable>();
        var sut = new RagPipeline(
            [_parser],
            _chunker,
            hybridStore,
            _embedder,
            chatClient: null,
            new ChunkingOptions());

        var queryEmbedding = new Embedding<float>(new float[] { 0.1f, 0.2f });
        var searchResult = new SearchResult
        {
            Chunk = new TextChunk { Text = "result", DocumentId = "doc-1", ChunkIndex = 0 },
            Score = 0.95,
        };

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));

        ((IHybridSearchable)hybridStore).HybridSearchAsync(
                Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { searchResult });

        var results = await sut.RetrieveAsync(
            "test query",
            new RetrievalOptions { UseHybridSearch = true },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        await ((IHybridSearchable)hybridStore).Received(1).HybridSearchAsync(
            "test query", Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_WithHybridSearch_ThrowsWhenStoreNotHybrid()
    {
        var queryEmbedding = new Embedding<float>(new float[] { 0.1f });
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.RetrieveAsync(
                "test query",
                new RetrievalOptions { UseHybridSearch = true },
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteAsync_DelegatesToVectorStore()
    {
        await _sut.DeleteAsync("doc-1", TestContext.Current.CancellationToken);
        await _vectorStore.Received(1).DeleteByDocumentIdAsync("doc-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_WithConversationHistory_IncludesHistoryInMessages()
    {
        var chatClient = Substitute.For<IChatClient>();
        var sut = new RagPipeline(
            [_parser],
            _chunker,
            _vectorStore,
            _embedder,
            chatClient,
            new ChunkingOptions());

        var queryEmbedding = new Embedding<float>(new float[] { 0.1f });
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));
        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer"));
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(response);

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Previous question"),
            new(ChatRole.Assistant, "Previous answer"),
        };

        await sut.AskAsync(
            "follow-up question",
            new RagOptions { ConversationHistory = history },
            TestContext.Current.CancellationToken);

        var capturedMessages = chatClient.ReceivedCalls()
            .First(c => string.Equals(c.GetMethodInfo().Name, nameof(IChatClient.GetResponseAsync), StringComparison.Ordinal))
            .GetArguments()[0] as IEnumerable<ChatMessage>;

        var list = capturedMessages!.ToList();
        Assert.Equal(4, list.Count);
        Assert.Equal(ChatRole.System, list[0].Role);
        Assert.Equal(ChatRole.User, list[1].Role);
        Assert.Equal("Previous question", list[1].Text);
        Assert.Equal(ChatRole.Assistant, list[2].Role);
        Assert.Equal("Previous answer", list[2].Text);
        Assert.Equal(ChatRole.User, list[3].Role);
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
