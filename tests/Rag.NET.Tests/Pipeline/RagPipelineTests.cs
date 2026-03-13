using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
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

        var result = await _sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

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
        await _sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

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
    public async Task RetrieveAsync_WithHybridSearch_FallsBackToBm25WhenStoreNotHybrid()
    {
        var queryEmbedding = new Embedding<float>(new float[] { 0.1f });
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));
        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        // _vectorStore is not IHybridSearchable — should fall back to dense+BM25, not throw
        var results = await _sut.RetrieveAsync(
            "test query",
            new RetrievalOptions { UseHybridSearch = true },
            TestContext.Current.CancellationToken);

        Assert.NotNull(results);
        await _vectorStore.Received(1).SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>());
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

    [Fact]
    public async Task IngestAsync_WithNullOptions_SkipsDeleteAndStoresChunks()
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

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello"));
        var result = await _sut.IngestAsync(stream, metadata, options: null, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ChunksStored);
        await _vectorStore.DidNotReceive().DeleteByDocumentIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_WithOverwriteTrue_DeletesBeforeStoring()
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

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello"));
        await _sut.IngestAsync(stream, metadata, new IngestionOptions { Overwrite = true }, cancellationToken: TestContext.Current.CancellationToken);

        await _vectorStore.Received(1).DeleteByDocumentIdAsync("doc-1", Arg.Any<CancellationToken>());
        await _vectorStore.Received(1).StoreAsync(Arg.Any<IReadOnlyList<EmbeddedChunk>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_WithOverwriteFalse_SkipsDelete()
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

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello"));
        await _sut.IngestAsync(stream, metadata, new IngestionOptions { Overwrite = false }, cancellationToken: TestContext.Current.CancellationToken);

        await _vectorStore.DidNotReceive().DeleteByDocumentIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_WithLostInTheMiddle_ReordersResults()
    {
        var embeddings = new GeneratedEmbeddings<Embedding<float>>(
            [new Embedding<float>(new float[] { 0.1f })]);
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(embeddings);

        var results = new List<SearchResult>
        {
            new() { Chunk = new TextChunk { Text = "a", DocumentId = "d", ChunkIndex = 0 }, Score = 0.9 },
            new() { Chunk = new TextChunk { Text = "b", DocumentId = "d", ChunkIndex = 1 }, Score = 0.8 },
            new() { Chunk = new TextChunk { Text = "c", DocumentId = "d", ChunkIndex = 2 }, Score = 0.7 },
        };
        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(results);

        var retrieved = await _sut.RetrieveAsync(
            "query",
            new RetrievalOptions { UseLostInTheMiddleReordering = true },
            TestContext.Current.CancellationToken);

        // [0.9, 0.8, 0.7] → [0.9, 0.7, 0.8]
        Assert.Equal(3, retrieved.Count);
        Assert.Equal(0.9, retrieved[0].Score);
        Assert.Equal(0.7, retrieved[1].Score);
        Assert.Equal(0.8, retrieved[2].Score);
    }

    [Fact]
    public async Task RetrieveAsync_WithoutLostInTheMiddle_PreservesOriginalOrder()
    {
        var embeddings = new GeneratedEmbeddings<Embedding<float>>(
            [new Embedding<float>(new float[] { 0.1f })]);
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(embeddings);

        var results = new List<SearchResult>
        {
            new() { Chunk = new TextChunk { Text = "a", DocumentId = "d", ChunkIndex = 0 }, Score = 0.9 },
            new() { Chunk = new TextChunk { Text = "b", DocumentId = "d", ChunkIndex = 1 }, Score = 0.8 },
            new() { Chunk = new TextChunk { Text = "c", DocumentId = "d", ChunkIndex = 2 }, Score = 0.7 },
        };
        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(results);

        var retrieved = await _sut.RetrieveAsync(
            "query",
            new RetrievalOptions { UseLostInTheMiddleReordering = false },
            TestContext.Current.CancellationToken);

        // Flag is false — original descending score order preserved
        Assert.Equal(3, retrieved.Count);
        Assert.Equal(0.9, retrieved[0].Score);
        Assert.Equal(0.8, retrieved[1].Score);
        Assert.Equal(0.7, retrieved[2].Score);
    }

    [Fact]
    public async Task AskAsync_WithLostInTheMiddle_ReordersRetrievedSources()
    {
        var chatClient = Substitute.For<IChatClient>();
        var sut = new RagPipeline([_parser], _chunker, _vectorStore, _embedder, chatClient, new ChunkingOptions());

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new() { Chunk = new TextChunk { Text = "a", DocumentId = "d", ChunkIndex = 0 }, Score = 0.9 },
                new() { Chunk = new TextChunk { Text = "b", DocumentId = "d", ChunkIndex = 1 }, Score = 0.8 },
                new() { Chunk = new TextChunk { Text = "c", DocumentId = "d", ChunkIndex = 2 }, Score = 0.7 },
            });

        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));

        var response = await sut.AskAsync("query", new RagOptions { UseLostInTheMiddleReordering = true }, TestContext.Current.CancellationToken);

        // [0.9, 0.8, 0.7] → reordered to [0.9, 0.7, 0.8]
        Assert.Equal(3, response.Sources.Count);
        Assert.Equal(0.9, response.Sources[0].Score);
        Assert.Equal(0.7, response.Sources[1].Score);
        Assert.Equal(0.8, response.Sources[2].Score);
    }

    [Fact]
    public async Task AskStreamingAsync_WithLostInTheMiddle_ReordersRetrievedSources()
    {
        var chatClient = Substitute.For<IChatClient>();
        var sut = new RagPipeline([_parser], _chunker, _vectorStore, _embedder, chatClient, new ChunkingOptions());

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new() { Chunk = new TextChunk { Text = "a", DocumentId = "d", ChunkIndex = 0 }, Score = 0.9 },
                new() { Chunk = new TextChunk { Text = "b", DocumentId = "d", ChunkIndex = 1 }, Score = 0.8 },
                new() { Chunk = new TextChunk { Text = "c", DocumentId = "d", ChunkIndex = 2 }, Score = 0.7 },
            });

        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new ChatResponseUpdate { Contents = [new TextContent("answer")] }));

        IReadOnlyList<SearchResult>? sources = null;
        await foreach (var update in sut.AskStreamingAsync("query", new RagOptions { UseLostInTheMiddleReordering = true }, TestContext.Current.CancellationToken))
        {
            if (update.Sources is not null)
                sources = update.Sources;
        }

        // [0.9, 0.8, 0.7] → reordered to [0.9, 0.7, 0.8]
        Assert.NotNull(sources);
        Assert.Equal(3, sources.Count);
        Assert.Equal(0.9, sources[0].Score);
        Assert.Equal(0.7, sources[1].Score);
        Assert.Equal(0.8, sources[2].Score);
    }

    [Fact]
    public void UseTokenAwareChunking_RegistersTokenAwareStrategy()
    {
        var services = new ServiceCollection();
        services.AddRagNet(b => b.UseTokenAwareChunking());

        var provider = services.BuildServiceProvider();
        var strategy = provider.GetService<IChunkingStrategy>();

        Assert.IsType<Rag.NET.Chunking.TokenAwareChunkingStrategy>(strategy);
    }

    [Fact]
    public void UseTokenAwareChunking_WithCustomModel_RegistersWithThatModel()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddRagNet(b => b.UseTokenAwareChunking("gpt-3.5-turbo"));

        var provider = services.BuildServiceProvider();
        var strategy = provider.GetRequiredService<Rag.NET.Abstractions.IChunkingStrategy>()
            as Rag.NET.Chunking.TokenAwareChunkingStrategy;

        Assert.NotNull(strategy);
        Assert.Equal("gpt-3.5-turbo", strategy.ModelName);
    }

    [Fact]
    public async Task IngestAsync_WithProgress_ReportsAllFourStagesInOrder()
    {
        var metadata = new DocumentMetadata { DocumentId = "doc-progress", FileName = "test.txt", ContentType = "text/plain" };
        var section = new DocumentSection { Text = "Hello world", DocumentId = "doc-progress", SectionIndex = 0 };
        var chunk = new TextChunk { Text = "Hello world", DocumentId = "doc-progress", ChunkIndex = 0 };
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(section));
        _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk));
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        var reported = new List<IngestionProgress>();
        var progress = new SynchronousProgress<IngestionProgress>(p => reported.Add(p));

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello world"));
        await _sut.IngestAsync(stream, metadata, progress: progress, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4, reported.Count);
        Assert.Equal(IngestionProgressStage.Parsing, reported[0].Stage);
        Assert.Equal(IngestionProgressStage.Chunking, reported[1].Stage);
        Assert.Equal(IngestionProgressStage.Embedding, reported[2].Stage);
        Assert.Equal(IngestionProgressStage.Storing, reported[3].Stage);
        Assert.All(reported, p => Assert.Equal("doc-progress", p.DocumentId));
    }

    [Fact]
    public async Task IngestAsync_WithProgress_ZeroChunks_ReportsOnlyParsingAndChunking()
    {
        var metadata = new DocumentMetadata { DocumentId = "doc-empty", FileName = "test.txt", ContentType = "text/plain" };
        var section = new DocumentSection { Text = "empty", DocumentId = "doc-empty", SectionIndex = 0 };

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(section));
        _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<TextChunk>()); // no chunks

        var reported = new List<IngestionProgress>();
        var progress = new SynchronousProgress<IngestionProgress>(p => reported.Add(p));

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("empty"));
        var result = await _sut.IngestAsync(stream, metadata, progress: progress, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, reported.Count);
        Assert.Equal(IngestionProgressStage.Parsing, reported[0].Stage);
        Assert.Equal(IngestionProgressStage.Chunking, reported[1].Stage);
        Assert.Equal(0, result.ChunksStored);
        await _vectorStore.DidNotReceive().StoreAsync(Arg.Any<IReadOnlyList<EmbeddedChunk>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_WithProgress_ReportsChunkCount()
    {
        var metadata = new DocumentMetadata { DocumentId = "doc-count", FileName = "test.txt", ContentType = "text/plain" };
        var section = new DocumentSection { Text = "text", DocumentId = "doc-count", SectionIndex = 0 };
        var chunk = new TextChunk { Text = "text", DocumentId = "doc-count", ChunkIndex = 0 };
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(section));
        _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk));
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        var reported = new List<IngestionProgress>();
        var progress = new SynchronousProgress<IngestionProgress>(p => reported.Add(p));

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("text"));
        await _sut.IngestAsync(stream, metadata, progress: progress, cancellationToken: TestContext.Current.CancellationToken);

        var chunkingReport = reported.First(p => p.Stage == IngestionProgressStage.Chunking);
        Assert.Equal(1, chunkingReport.Current);
        Assert.Equal(1, chunkingReport.Total);

        var storingReport = reported.First(p => p.Stage == IngestionProgressStage.Storing);
        Assert.Equal(1, storingReport.Current);
        Assert.Equal(1, storingReport.Total);
    }

    [Fact]
    public async Task IngestAsync_SectionWithHeading_PropagatesMetadataToChunks()
    {
        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
        var section = new DocumentSection
        {
            Text = "Section content",
            DocumentId = "doc-1",
            SectionIndex = 0,
            HeadingLevel = 2,
            Heading = "My Section",
        };
        var chunk = new TextChunk { Text = "Section content", DocumentId = "doc-1", ChunkIndex = 0 };
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(section));
        _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk));
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        IReadOnlyList<EmbeddedChunk>? captured = null;
        await _vectorStore.StoreAsync(
            Arg.Do<IReadOnlyList<EmbeddedChunk>>(c => captured = c),
            Arg.Any<CancellationToken>());

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Section content"));
        await _sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        var chunkMetadata = captured![0].Chunk.Metadata;
        Assert.Equal("My Section", chunkMetadata["heading"]);
        Assert.Equal("2", chunkMetadata["heading_level"]);
        Assert.Equal("My Section", chunkMetadata["heading_breadcrumb"]);
    }

    [Fact]
    public async Task IngestAsync_NestedHeadings_BuildsBreadcrumb()
    {
        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
        var section1 = new DocumentSection
        {
            Text = "Chapter content",
            DocumentId = "doc-1",
            SectionIndex = 0,
            HeadingLevel = 1,
            Heading = "Chapter 1",
        };
        var section2 = new DocumentSection
        {
            Text = "Overview content",
            DocumentId = "doc-1",
            SectionIndex = 1,
            HeadingLevel = 2,
            Heading = "Overview",
        };
        var chunk1 = new TextChunk { Text = "Chapter content", DocumentId = "doc-1", ChunkIndex = 0 };
        var chunk2 = new TextChunk { Text = "Overview content", DocumentId = "doc-1", ChunkIndex = 1 };
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(section1, section2));
        _chunker.ChunkAsync(section1, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk1));
        _chunker.ChunkAsync(section2, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk2));
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding, embedding]));

        IReadOnlyList<EmbeddedChunk>? captured = null;
        await _vectorStore.StoreAsync(
            Arg.Do<IReadOnlyList<EmbeddedChunk>>(c => captured = c),
            Arg.Any<CancellationToken>());

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("content"));
        await _sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        var secondChunkMetadata = captured![1].Chunk.Metadata;
        Assert.Equal("Chapter 1 > Overview", secondChunkMetadata["heading_breadcrumb"]);
    }

    [Fact]
    public async Task IngestAsync_SectionWithoutHeading_NoHeadingMetadata()
    {
        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
        var section = new DocumentSection
        {
            Text = "Plain content",
            DocumentId = "doc-1",
            SectionIndex = 0,
            // No HeadingLevel, no Heading
        };
        var chunk = new TextChunk { Text = "Plain content", DocumentId = "doc-1", ChunkIndex = 0 };
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(section));
        _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk));
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        IReadOnlyList<EmbeddedChunk>? captured = null;
        await _vectorStore.StoreAsync(
            Arg.Do<IReadOnlyList<EmbeddedChunk>>(c => captured = c),
            Arg.Any<CancellationToken>());

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Plain content"));
        await _sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        var chunkMetadata = captured![0].Chunk.Metadata;
        Assert.False(chunkMetadata.ContainsKey("heading"), "heading key should not be present for sections without headings");
        Assert.False(chunkMetadata.ContainsKey("heading_breadcrumb"), "heading_breadcrumb key should not be present for sections without headings");
    }

    [Fact]
    public async Task RetrieveAsync_WithRedundancyFilter_DropsRedundantChunks()
    {
        // Both chunks get the same vector → cosine similarity == 1.0 ≥ 0.95 → second is dropped
        var sharedEmbedding = new Embedding<float>(new float[] { 1f, 0f, 0f });
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([sharedEmbedding, sharedEmbedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new() { Chunk = new TextChunk { Text = "a", DocumentId = "d", ChunkIndex = 0 }, Score = 0.9 },
                new() { Chunk = new TextChunk { Text = "b", DocumentId = "d", ChunkIndex = 1 }, Score = 0.8 },
            });

        var results = await _sut.RetrieveAsync(
            "q",
            new RetrievalOptions { UseRedundancyFilter = true, RedundancyThreshold = 0.95f },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("a", results[0].Chunk.Text);
    }

    [Fact]
    public async Task RetrieveAsync_WithoutRedundancyFilter_KeepsAll()
    {
        // Same vector for everything, but filter is disabled → both results kept
        var sharedEmbedding = new Embedding<float>(new float[] { 1f, 0f, 0f });
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([sharedEmbedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new() { Chunk = new TextChunk { Text = "a", DocumentId = "d", ChunkIndex = 0 }, Score = 0.9 },
                new() { Chunk = new TextChunk { Text = "b", DocumentId = "d", ChunkIndex = 1 }, Score = 0.8 },
            });

        var results = await _sut.RetrieveAsync(
            "q",
            new RetrievalOptions { UseRedundancyFilter = false },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task AskAsync_WithRedundancyFilter_DropsRedundantSources()
    {
        var chatClient = Substitute.For<IChatClient>();
        var sut = new RagPipeline([_parser], _chunker, _vectorStore, _embedder, chatClient, new ChunkingOptions());

        // Same vector for query embed and re-embed of both chunks → cosine similarity == 1.0 ≥ 0.95 → second dropped
        var sharedEmbedding = new Embedding<float>(new float[] { 1f, 0f, 0f });
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([sharedEmbedding, sharedEmbedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new() { Chunk = new TextChunk { Text = "a", DocumentId = "d", ChunkIndex = 0 }, Score = 0.9 },
                new() { Chunk = new TextChunk { Text = "b", DocumentId = "d", ChunkIndex = 1 }, Score = 0.8 },
            });

        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));

        var response = await sut.AskAsync(
            "q",
            new RagOptions { UseRedundancyFilter = true, RedundancyThreshold = 0.95f },
            TestContext.Current.CancellationToken);

        var source = Assert.Single(response.Sources);
        Assert.Equal("a", source.Chunk.Text);
    }

    [Fact]
    public async Task AskStreamingAsync_WithRedundancyFilter_DropsRedundantSources()
    {
        var chatClient = Substitute.For<IChatClient>();
        var sut = new RagPipeline([_parser], _chunker, _vectorStore, _embedder, chatClient, new ChunkingOptions());

        // Same vector for query embed and re-embed of both chunks → cosine similarity == 1.0 ≥ 0.95 → second dropped
        var sharedEmbedding = new Embedding<float>(new float[] { 1f, 0f, 0f });
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([sharedEmbedding, sharedEmbedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new() { Chunk = new TextChunk { Text = "a", DocumentId = "d", ChunkIndex = 0 }, Score = 0.9 },
                new() { Chunk = new TextChunk { Text = "b", DocumentId = "d", ChunkIndex = 1 }, Score = 0.8 },
            });

        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new ChatResponseUpdate { Contents = [new TextContent("answer")] }));

        IReadOnlyList<SearchResult>? sources = null;
        await foreach (var update in sut.AskStreamingAsync("q", new RagOptions { UseRedundancyFilter = true, RedundancyThreshold = 0.95f }, TestContext.Current.CancellationToken))
        {
            if (update.Sources is not null)
                sources = update.Sources;
        }

        Assert.NotNull(sources);
        var source = Assert.Single(sources);
        Assert.Equal("a", source.Chunk.Text);
    }

    [Fact]
    public async Task RetrieveAsync_WithBothLostInTheMiddleAndRedundancyFilter_AppliesReorderingThenFilters()
    {
        // LITM reorders [a(0.9), b(0.8), c(0.7)] → [a(0.9), c(0.7), b(0.8)]
        // RedundancyFilter re-embeds [a, c, b]: a=[1,0,0], c=[1,0,0] (redundant to a), b=[0,1,0] (kept)
        // Result: [a, b]
        var vectorA = new float[] { 1f, 0f, 0f };
        var vectorC = new float[] { 1f, 0f, 0f }; // identical to a → redundant
        var vectorB = new float[] { 0f, 1f, 0f }; // orthogonal → kept

        // First call: query embed → returns [1,0,0] (only index 0 used)
        // Second call: re-embed [a, c, b] → returns [vectorA, vectorC, vectorB]
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(vectorA)]),
                new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(vectorA), new Embedding<float>(vectorC), new Embedding<float>(vectorB)]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new() { Chunk = new TextChunk { Text = "a", DocumentId = "d", ChunkIndex = 0 }, Score = 0.9 },
                new() { Chunk = new TextChunk { Text = "b", DocumentId = "d", ChunkIndex = 1 }, Score = 0.8 },
                new() { Chunk = new TextChunk { Text = "c", DocumentId = "d", ChunkIndex = 2 }, Score = 0.7 },
            });

        var results = await _sut.RetrieveAsync(
            "q",
            new RetrievalOptions { UseLostInTheMiddleReordering = true, UseRedundancyFilter = true, RedundancyThreshold = 0.95f },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("a", results[0].Chunk.Text);
        Assert.Equal("b", results[1].Chunk.Text);
    }

    [Fact]
    public async Task RetrieveAsync_WithRedundancyFilter_AllResultsRedundant_ReturnsFirstOnly()
    {
        // All 3 chunks get the same vector → all redundant to the first → only first kept
        var sharedEmbedding = new Embedding<float>(new float[] { 1f, 0f, 0f });
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([sharedEmbedding, sharedEmbedding, sharedEmbedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new() { Chunk = new TextChunk { Text = "x", DocumentId = "d", ChunkIndex = 0 }, Score = 0.9 },
                new() { Chunk = new TextChunk { Text = "x", DocumentId = "d", ChunkIndex = 1 }, Score = 0.8 },
                new() { Chunk = new TextChunk { Text = "x", DocumentId = "d", ChunkIndex = 2 }, Score = 0.7 },
            });

        var results = await _sut.RetrieveAsync(
            "q",
            new RetrievalOptions { UseRedundancyFilter = true, RedundancyThreshold = 0.95f },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
    }

    [Fact]
    public async Task IngestAsync_SectionWithHeadingLevelZero_NoHeadingMetadata()
    {
        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
        var section = new DocumentSection
        {
            Text = "Bad level content",
            DocumentId = "doc-1",
            SectionIndex = 0,
            HeadingLevel = 0,
            Heading = "Bad",
        };
        var chunk = new TextChunk { Text = "Bad level content", DocumentId = "doc-1", ChunkIndex = 0 };
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(section));
        _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk));
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        IReadOnlyList<EmbeddedChunk>? captured = null;
        await _vectorStore.StoreAsync(
            Arg.Do<IReadOnlyList<EmbeddedChunk>>(c => captured = c),
            Arg.Any<CancellationToken>());

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Bad level content"));
        await _sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        var chunkMetadata = captured![0].Chunk.Metadata;
        Assert.False(chunkMetadata.ContainsKey("heading"), "heading key should not be present for out-of-range heading level 0");
    }

    [Fact]
    public async Task IngestAsync_SectionWithHeadingLevelSeven_NoHeadingMetadata()
    {
        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
        var section = new DocumentSection
        {
            Text = "Bad level content",
            DocumentId = "doc-1",
            SectionIndex = 0,
            HeadingLevel = 7,
            Heading = "Bad",
        };
        var chunk = new TextChunk { Text = "Bad level content", DocumentId = "doc-1", ChunkIndex = 0 };
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(section));
        _chunker.ChunkAsync(section, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk));
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        IReadOnlyList<EmbeddedChunk>? captured = null;
        await _vectorStore.StoreAsync(
            Arg.Do<IReadOnlyList<EmbeddedChunk>>(c => captured = c),
            Arg.Any<CancellationToken>());

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Bad level content"));
        await _sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        var chunkMetadata = captured![0].Chunk.Metadata;
        Assert.False(chunkMetadata.ContainsKey("heading"), "heading key should not be present for out-of-range heading level 7");
    }

    [Fact]
    public async Task IngestAsync_SkipLevelHeadings_BreadcrumbSkipsEmptyLevels()
    {
        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
        var section1 = new DocumentSection
        {
            Text = "Root content",
            DocumentId = "doc-1",
            SectionIndex = 0,
            HeadingLevel = 1,
            Heading = "Root",
        };
        var section2 = new DocumentSection
        {
            Text = "Deep content",
            DocumentId = "doc-1",
            SectionIndex = 1,
            HeadingLevel = 3,
            Heading = "Deep",
        };
        var chunk1 = new TextChunk { Text = "Root content", DocumentId = "doc-1", ChunkIndex = 0 };
        var chunk2 = new TextChunk { Text = "Deep content", DocumentId = "doc-1", ChunkIndex = 1 };
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(section1, section2));
        _chunker.ChunkAsync(section1, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk1));
        _chunker.ChunkAsync(section2, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk2));
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding, embedding]));

        IReadOnlyList<EmbeddedChunk>? captured = null;
        await _vectorStore.StoreAsync(
            Arg.Do<IReadOnlyList<EmbeddedChunk>>(c => captured = c),
            Arg.Any<CancellationToken>());

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("content"));
        await _sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        var deepChunkMetadata = captured![1].Chunk.Metadata;
        Assert.Equal("Root > Deep", deepChunkMetadata["heading_breadcrumb"]);
    }

    [Fact]
    public async Task IngestAsync_MultipleTopLevelHeadings_BreadcrumbResetsCorrectly()
    {
        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
        var section1 = new DocumentSection
        {
            Text = "Part 1 content",
            DocumentId = "doc-1",
            SectionIndex = 0,
            HeadingLevel = 1,
            Heading = "Part 1",
        };
        var section2 = new DocumentSection
        {
            Text = "Chapter A content",
            DocumentId = "doc-1",
            SectionIndex = 1,
            HeadingLevel = 2,
            Heading = "Chapter A",
        };
        var section3 = new DocumentSection
        {
            Text = "Part 2 content",
            DocumentId = "doc-1",
            SectionIndex = 2,
            HeadingLevel = 1,
            Heading = "Part 2",
        };
        var chunk1 = new TextChunk { Text = "Part 1 content", DocumentId = "doc-1", ChunkIndex = 0 };
        var chunk2 = new TextChunk { Text = "Chapter A content", DocumentId = "doc-1", ChunkIndex = 1 };
        var chunk3 = new TextChunk { Text = "Part 2 content", DocumentId = "doc-1", ChunkIndex = 2 };
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(section1, section2, section3));
        _chunker.ChunkAsync(section1, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk1));
        _chunker.ChunkAsync(section2, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk2));
        _chunker.ChunkAsync(section3, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk3));
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding, embedding, embedding]));

        IReadOnlyList<EmbeddedChunk>? captured = null;
        await _vectorStore.StoreAsync(
            Arg.Do<IReadOnlyList<EmbeddedChunk>>(c => captured = c),
            Arg.Any<CancellationToken>());

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("content"));
        await _sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        var part2ChunkMetadata = captured![2].Chunk.Metadata;
        Assert.Equal("Part 2", part2ChunkMetadata["heading_breadcrumb"]);
    }

    [Fact]
    public async Task IngestAsync_MultipleCHunksFromSameSection_AllGetSameHeadingMetadata()
    {
        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt", ContentType = "text/plain" };
        var section = new DocumentSection
        {
            Text = "My Section content",
            DocumentId = "doc-1",
            SectionIndex = 0,
            HeadingLevel = 2,
            Heading = "My Section",
        };
        var chunk1 = new TextChunk { Text = "chunk1", DocumentId = "doc-1", ChunkIndex = 0 };
        var chunk2 = new TextChunk { Text = "chunk2", DocumentId = "doc-1", ChunkIndex = 1 };
        var chunk3 = new TextChunk { Text = "chunk3", DocumentId = "doc-1", ChunkIndex = 2 };
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(section));
        _chunker.ChunkAsync(Arg.Any<DocumentSection>(), Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk1, chunk2, chunk3));
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding, embedding, embedding]));

        IReadOnlyList<EmbeddedChunk>? captured = null;
        await _vectorStore.StoreAsync(
            Arg.Do<IReadOnlyList<EmbeddedChunk>>(c => captured = c),
            Arg.Any<CancellationToken>());

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("My Section content"));
        await _sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(3, captured!.Count);
        foreach (var embeddedChunk in captured)
        {
            var chunkMetadata = embeddedChunk.Chunk.Metadata;
            Assert.Equal("My Section", chunkMetadata["heading"]);
            Assert.Equal("My Section", chunkMetadata["heading_breadcrumb"]);
        }
    }

    [Fact]
    public async Task RetrieveAsync_WithHybridSearch_AndNonHybridStore_DoesNotThrow()
    {
        // Arrange: ingest a document
        var metadata = new DocumentMetadata { DocumentId = "doc-hybrid", FileName = "test.txt", ContentType = "text/plain" };
        var section = new DocumentSection { Text = "the quick brown fox", DocumentId = "doc-hybrid", SectionIndex = 0 };
        var chunk = new TextChunk { Text = "the quick brown fox", DocumentId = "doc-hybrid", ChunkIndex = 0 };
        var embedding = new Embedding<float>(new float[] { 0.1f, 0.2f, 0.3f });

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(section));
        _chunker.ChunkAsync(Arg.Any<DocumentSection>(), Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk));
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GeneratedEmbeddings<Embedding<float>> { embedding }));
        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        await _sut.IngestAsync(new MemoryStream(), metadata, cancellationToken: TestContext.Current.CancellationToken);

        // Act: _vectorStore is NOT IHybridSearchable — should fall back, not throw
        var results = await _sut.RetrieveAsync("fox", new RetrievalOptions { UseHybridSearch = true }, TestContext.Current.CancellationToken);

        // Dense search was called
        await _vectorStore.Received(1).SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>());
        // No exception — BM25 fallback worked
        Assert.NotNull(results);
    }

    [Fact]
    public async Task DeleteAsync_RemovesBm25Entries_SoSubsequentHybridSearchFindsNothing()
    {
        var metadata = new DocumentMetadata { DocumentId = "doc-del", FileName = "test.txt", ContentType = "text/plain" };
        var section = new DocumentSection { Text = "fox jumps", DocumentId = "doc-del", SectionIndex = 0 };
        var chunk = new TextChunk { Text = "fox jumps", DocumentId = "doc-del", ChunkIndex = 0 };
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(section));
        _chunker.ChunkAsync(Arg.Any<DocumentSection>(), Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunk));
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GeneratedEmbeddings<Embedding<float>> { embedding }));
        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        await _sut.IngestAsync(new MemoryStream(), metadata, cancellationToken: TestContext.Current.CancellationToken);
        await _sut.DeleteAsync("doc-del", TestContext.Current.CancellationToken);

        var results = await _sut.RetrieveAsync("fox", new RetrievalOptions { UseHybridSearch = true }, TestContext.Current.CancellationToken);
        Assert.Empty(results);
    }

    [Fact]
    public async Task IngestAsync_WithOverwrite_ClearsBm25BeforeReIndexing()
    {
        var metadata = new DocumentMetadata { DocumentId = "doc-ow", FileName = "test.txt", ContentType = "text/plain" };

        var sectionV1 = new DocumentSection { Text = "tiger stalks prey", DocumentId = "doc-ow", SectionIndex = 0 };
        var chunkV1 = new TextChunk { Text = "tiger stalks prey", DocumentId = "doc-ow", ChunkIndex = 0 };

        var sectionV2 = new DocumentSection { Text = "elephant roams savanna", DocumentId = "doc-ow", SectionIndex = 0 };
        var chunkV2 = new TextChunk { Text = "elephant roams savanna", DocumentId = "doc-ow", ChunkIndex = 0 };

        var embedding = new Embedding<float>(new float[] { 0.1f });

        // First call returns V1, second returns V2
        _parser.ParseAsync(Arg.Any<Stream>(), metadata, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(sectionV1), ToAsyncEnumerable(sectionV2));
        _chunker.ChunkAsync(sectionV1, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunkV1));
        _chunker.ChunkAsync(sectionV2, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunkV2));
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GeneratedEmbeddings<Embedding<float>> { embedding }));
        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        // Ingest V1
        await _sut.IngestAsync(new MemoryStream(), metadata, cancellationToken: TestContext.Current.CancellationToken);
        // Overwrite with V2
        await _sut.IngestAsync(new MemoryStream(), metadata, new IngestionOptions { Overwrite = true }, cancellationToken: TestContext.Current.CancellationToken);

        // "tiger" from V1 should be gone
        var results = await _sut.RetrieveAsync("tiger", new RetrievalOptions { UseHybridSearch = true }, TestContext.Current.CancellationToken);
        Assert.Empty(results);

        // V2's "elephant" term should now be in the index
        var elephantResults = await _sut.RetrieveAsync("elephant", new RetrievalOptions { UseHybridSearch = true }, TestContext.Current.CancellationToken);
        Assert.NotEmpty(elephantResults);
    }

    // Gap 5 — TopK propagation in hybrid fallback
    [Fact]
    public async Task RetrieveAsync_HybridFallback_RespectsTopK()
    {
        // Ingest 5 documents so BM25 has entries for "hello"
        var embedding = new Embedding<float>(new float[] { 0.1f });
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));
        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        for (int i = 0; i < 5; i++)
        {
            var meta = new DocumentMetadata { DocumentId = $"doc-topk-{i}", FileName = "t.txt", ContentType = "text/plain" };
            var sec = new DocumentSection { Text = "hello world", DocumentId = $"doc-topk-{i}", SectionIndex = 0 };
            var chk = new TextChunk { Text = "hello world", DocumentId = $"doc-topk-{i}", ChunkIndex = 0 };
            _parser.ParseAsync(Arg.Any<Stream>(), meta, Arg.Any<CancellationToken>())
                .Returns(ToAsyncEnumerable(sec));
            _chunker.ChunkAsync(sec, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
                .Returns(ToAsyncEnumerable(chk));
            await _sut.IngestAsync(new MemoryStream(), meta, cancellationToken: TestContext.Current.CancellationToken);
        }

        // Reset embedder for the query call
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        var results = await _sut.RetrieveAsync(
            "hello",
            new RetrievalOptions { UseHybridSearch = true, TopK = 2 },
            TestContext.Current.CancellationToken);

        Assert.True(results.Count <= 2, $"Expected ≤ 2 results but got {results.Count}");
    }

    // Gap 6 — hybrid search where only BM25 returns results (dense returns empty)
    [Fact]
    public async Task RetrieveAsync_HybridFallback_WhenDenseEmpty_BM25SurfacesResults()
    {
        var embedding = new Embedding<float>(new float[] { 0.1f });
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));
        // Dense always returns empty
        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var meta = new DocumentMetadata { DocumentId = "doc-bm25-only", FileName = "t.txt", ContentType = "text/plain" };
        var sec = new DocumentSection { Text = "unique keyword jaguar", DocumentId = "doc-bm25-only", SectionIndex = 0 };
        var chk = new TextChunk { Text = "unique keyword jaguar", DocumentId = "doc-bm25-only", ChunkIndex = 0 };
        _parser.ParseAsync(Arg.Any<Stream>(), meta, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(sec));
        _chunker.ChunkAsync(sec, Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chk));

        await _sut.IngestAsync(new MemoryStream(), meta, cancellationToken: TestContext.Current.CancellationToken);

        // Reset embedder for query call
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        var results = await _sut.RetrieveAsync(
            "jaguar",
            new RetrievalOptions { UseHybridSearch = true },
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(results);
    }

    // Gap 7 — DeleteAsync on never-ingested document does not throw
    [Fact]
    public async Task DeleteAsync_OnNonExistentDocument_DoesNotThrow()
    {
        // No ingestion — calling delete should not throw
        var exception = await Record.ExceptionAsync(() =>
            _sut.DeleteAsync("nonexistent-id", TestContext.Current.CancellationToken));

        Assert.Null(exception);
    }

    private sealed class SynchronousProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(params T[] items)
    {
        foreach (var item in items)
        {
            yield return item;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    [Fact]
    public async Task RetrieveAsync_WithMultiQueryExpander_DeduplicatesByChunkKeepingHighestScore()
    {
        var expander = Substitute.For<IQueryExpander>();
        expander.ExpandAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(["variant 1"]);

        var sharedChunk = new TextChunk { Text = "shared", DocumentId = "doc-1", ChunkIndex = 0 };
        var uniqueChunk = new TextChunk { Text = "unique", DocumentId = "doc-2", ChunkIndex = 0 };
        var embedding = new Embedding<float>(new float[] { 0.1f, 0.2f });

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        // First SearchAsync call (original query): sharedChunk at 0.9
        // Second SearchAsync call (variant 1): sharedChunk at 0.5 + uniqueChunk at 0.8
        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                [new SearchResult { Chunk = sharedChunk, Score = 0.9 }],
                [new SearchResult { Chunk = sharedChunk, Score = 0.5 }, new SearchResult { Chunk = uniqueChunk, Score = 0.8 }]);

        var sut = new RagPipeline([_parser], _chunker, _vectorStore, _embedder, chatClient: null, new ChunkingOptions(), queryExpander: expander);

        var results = await sut.RetrieveAsync("what is rag?", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal(0.9, results[0].Score); // sharedChunk: highest score wins
        Assert.Equal(0.8, results[1].Score); // uniqueChunk
    }

    [Fact]
    public async Task RetrieveAsync_WithUseMultiQueryFalse_SkipsExpansion()
    {
        var expander = Substitute.For<IQueryExpander>();
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var sut = new RagPipeline([_parser], _chunker, _vectorStore, _embedder, chatClient: null, new ChunkingOptions(), queryExpander: expander);

        await sut.RetrieveAsync("query", new RetrievalOptions { UseMultiQuery = false }, TestContext.Current.CancellationToken);

        await expander.DidNotReceive().ExpandAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_WhenExpanderThrows_FallsBackToSingleQuery()
    {
        var expander = Substitute.For<IQueryExpander>();
        expander.ExpandAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("LLM unreachable"));

        var chunk = new TextChunk { Text = "result", DocumentId = "doc-1", ChunkIndex = 0 };
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns([new SearchResult { Chunk = chunk, Score = 0.9 }]);

        var sut = new RagPipeline([_parser], _chunker, _vectorStore, _embedder, chatClient: null, new ChunkingOptions(), queryExpander: expander);

        var results = await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(results);
        await _vectorStore.Received(1).SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_WhenExpanderReturnsEmptyList_FallsBackToOriginalQuery()
    {
        var expander = Substitute.For<IQueryExpander>();
        expander.ExpandAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());

        var chunk = new TextChunk { Text = "result", DocumentId = "doc-1", ChunkIndex = 0 };
        var embedding = new Embedding<float>(new float[] { 0.1f });

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns([new SearchResult { Chunk = chunk, Score = 0.9 }]);

        var sut = new RagPipeline([_parser], _chunker, _vectorStore, _embedder, chatClient: null, new ChunkingOptions(), queryExpander: expander);

        var results = await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        // Empty variants → only original query runs → 1 SearchAsync call, 1 result
        Assert.Single(results);
        await _vectorStore.Received(1).SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_OriginalQueryAlwaysIncludedInFanOut()
    {
        var expander = Substitute.For<IQueryExpander>();
        expander.ExpandAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(["variant 1", "variant 2"]);

        var embedding = new Embedding<float>(new float[] { 0.1f });

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        _vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var sut = new RagPipeline([_parser], _chunker, _vectorStore, _embedder, chatClient: null, new ChunkingOptions(), queryExpander: expander);

        await sut.RetrieveAsync("original", cancellationToken: TestContext.Current.CancellationToken);

        // 3 queries total: original + 2 variants
        await _vectorStore.Received(3).SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>());
    }
}
