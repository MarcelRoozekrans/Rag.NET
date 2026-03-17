using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddRagNet_RegistersIRagPipeline()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IVectorStore>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());

        services.AddRagNet();

        var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IRagPipeline>());
    }

    [Fact]
    public void AddRagNet_RegistersIRetriever()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IVectorStore>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());

        services.AddRagNet();

        var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IRetriever>());
    }

    [Fact]
    public void AddRagNet_RegistersIIngestor()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IVectorStore>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());

        services.AddRagNet();

        var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IIngestor>());
    }

    [Fact]
    public async Task AddRagNet_WithReranking_ChainsRerankingRetriever()
    {
        var services = new ServiceCollection();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var reranker = Substitute.For<IReranker>();

        services.AddSingleton(vectorStore);
        services.AddSingleton(embedder);
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());
        reranker.RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>())
            .Returns(new List<RerankResult>());

        services.AddRagNet();
        services.AddSingleton(reranker);

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        await pipeline.RetrieveAsync("query", new RetrievalOptions { UseReranking = true }, TestContext.Current.CancellationToken);

        await reranker.Received(1).RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddRagNet_WithMultiQuery_ChainsMultiQueryRetriever()
    {
        var services = new ServiceCollection();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var queryExpander = Substitute.For<IQueryExpander>();

        services.AddSingleton(vectorStore);
        services.AddSingleton(embedder);
        services.AddSingleton(queryExpander);
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());
        queryExpander.ExpandAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "variant 1" });

        services.AddRagNet();

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        await pipeline.RetrieveAsync("query", new RetrievalOptions { UseMultiQuery = true }, TestContext.Current.CancellationToken);

        await queryExpander.Received(1).ExpandAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddRagNet_WithHyde_ChainsHydeRetriever()
    {
        var services = new ServiceCollection();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var hydeGenerator = Substitute.For<IHypotheticalDocumentGenerator>();

        services.AddSingleton(vectorStore);
        services.AddSingleton(embedder);
        services.AddSingleton(hydeGenerator);

        hydeGenerator.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("hypothetical document");
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        services.AddRagNet();

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        await pipeline.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        await hydeGenerator.Received(1).GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddRagNet_WithAllFeatures_ChainsFullDecoratorPipeline()
    {
        var services = new ServiceCollection();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var reranker = Substitute.For<IReranker>();
        var queryExpander = Substitute.For<IQueryExpander>();
        var hydeGenerator = Substitute.For<IHypotheticalDocumentGenerator>();

        services.AddSingleton(vectorStore);
        services.AddSingleton(embedder);
        services.AddSingleton(reranker);
        services.AddSingleton(queryExpander);
        services.AddSingleton(hydeGenerator);

        hydeGenerator.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("hypothetical document");
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());
        queryExpander.ExpandAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "variant" });
        reranker.RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>())
            .Returns(new List<RerankResult>());

        services.AddRagNet();

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        var opts = new RetrievalOptions
        {
            UseMultiQuery = true,
            UseReranking = true,
            UseRedundancyFilter = true,
            UseLostInTheMiddleReordering = true,
            UseHyde = true,
        };

        await pipeline.RetrieveAsync("query", opts, TestContext.Current.CancellationToken);

        await hydeGenerator.Received().GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await queryExpander.Received(1).ExpandAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await reranker.Received(1).RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddRagNet_WithCaching_CachesSecondCall()
    {
        var services = new ServiceCollection();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        services.AddSingleton(vectorStore);
        services.AddSingleton(embedder);
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        services.AddRagNet(b => b.UseCaching());

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        await pipeline.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);
        await pipeline.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        // Second call should be cached — embedder called only once
        await embedder.Received(1).GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddRagNet_WithoutCaching_DoesNotCacheSecondCall()
    {
        var services = new ServiceCollection();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        services.AddSingleton(vectorStore);
        services.AddSingleton(embedder);
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        services.AddRagNet(); // no UseCaching()

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        await pipeline.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);
        await pipeline.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        // Without caching, embedder should be called twice
        await embedder.Received(2).GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void AddRagNet_WithoutOptionalDeps_ResolvesPipeline()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IVectorStore>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());

        services.AddRagNet();

        var sp = services.BuildServiceProvider();
        var retriever = sp.GetRequiredService<IRetriever>();
        var ingestor = sp.GetRequiredService<IIngestor>();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        Assert.NotNull(retriever);
        Assert.NotNull(ingestor);
        Assert.NotNull(pipeline);
    }

    [Fact]
    public async Task AddRagNet_WithParentDocumentRetrieval_ReplacesChildWithParentText()
    {
        var services = new ServiceCollection();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        services.AddSingleton(vectorStore);
        services.AddSingleton(embedder);
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new()
                {
                    Chunk = new TextChunk
                    {
                        Text = "small child", DocumentId = "doc1", ChunkIndex = 0,
                        Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["_parentKey"] = "doc1:0" }
                    },
                    Score = 0.9
                }
            });

        services.AddRagNet(b => b.UseParentDocumentRetrieval());

        var sp = services.BuildServiceProvider();

        // Manually add parent text to the store
        var parentStore = sp.GetRequiredService<Rag.NET.Abstractions.IParentChunkStore>();
        parentStore.Add("doc1", 0, "large parent context text");

        var pipeline = sp.GetRequiredService<IRagPipeline>();
        var results = await pipeline.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("large parent context text", results[0].Chunk.Text);
    }

    [Fact]
    public async Task AddRagNet_WithoutParentDocumentRetrieval_ReturnsChildText()
    {
        var services = new ServiceCollection();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        services.AddSingleton(vectorStore);
        services.AddSingleton(embedder);
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new()
                {
                    Chunk = new TextChunk
                    {
                        Text = "small child", DocumentId = "doc1", ChunkIndex = 0,
                    },
                    Score = 0.9
                }
            });

        services.AddRagNet(); // no UseParentDocumentRetrieval

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();
        var results = await pipeline.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("small child", results[0].Chunk.Text);
    }

    [Fact]
    public async Task AddRagNet_WithMmr_CallsEmbedderTwiceForMmrSelection()
    {
        var services = new ServiceCollection();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        services.AddSingleton(vectorStore);
        services.AddSingleton(embedder);

        var singleVec = new float[] { 1f, 0f };
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var count = ci.Arg<IEnumerable<string>>().Count();
                var embeddings = Enumerable.Range(0, count)
                    .Select(_ => new Embedding<float>(singleVec))
                    .ToList();
                return new GeneratedEmbeddings<Embedding<float>>(embeddings);
            });
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new() { Chunk = new TextChunk { Text = "candidate doc", DocumentId = "doc1", ChunkIndex = 0 }, Score = 0.9 }
            });

        services.AddRagNet(b => b.UseMmr());

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        await pipeline.RetrieveAsync("query", new RetrievalOptions { UseMmr = true },
            TestContext.Current.CancellationToken);

        // With UseMmr=true: embedder called once for vector search + once for query embedding in MMR
        await embedder.Received(2).GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddRagNet_WithSqlitePersistence_ReturnsChunksAfterSimulatedRestart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ragnet-di-test-{Guid.NewGuid():N}.db");
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // --- First "session" ---
            var services1 = new ServiceCollection();
            var vectorStore = Substitute.For<IVectorStore>();
            var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
            services1.AddSingleton(vectorStore);
            services1.AddSingleton(embedder);
            embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), ct)
                .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
            vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), ct)
                .Returns(new List<SearchResult>());

            services1.AddRagNet(b => b.UseSqlitePersistence(dbPath, "test-coll"));
            var sp1 = services1.BuildServiceProvider();

            // Ingest a chunk — this should write to SQLite
            var ingestor1 = sp1.GetRequiredService<IIngestor>();
            await ingestor1.IngestAsync(
                new MemoryStream(System.Text.Encoding.UTF8.GetBytes("hello world")),
                new Rag.NET.Models.DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt" },
                cancellationToken: ct);

            await sp1.DisposeAsync();

            // --- Second "session" (same db, same collection) ---
            var services2 = new ServiceCollection();
            services2.AddSingleton(vectorStore);
            services2.AddSingleton(embedder);
            services2.AddRagNet(b => b.UseSqlitePersistence(dbPath, "test-coll"));
            var sp2 = services2.BuildServiceProvider();

            var bm25 = sp2.GetRequiredService<IBm25Index>();
            var results = bm25.Search("hello", topK: 5);

            Assert.NotEmpty(results); // chunks loaded from SQLite without re-ingestion
            await sp2.DisposeAsync();
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task AddRagNet_WithSqlitePersistence_CollectionMismatch_WipesStaleData()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ragnet-stale-test-{Guid.NewGuid():N}.db");
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // --- Session 1: ingest with collection-A ---
            var services1 = new ServiceCollection();
            var vectorStore = Substitute.For<IVectorStore>();
            var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
            services1.AddSingleton(vectorStore);
            services1.AddSingleton(embedder);
            embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), ct)
                .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
            vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), ct)
                .Returns(new List<SearchResult>());

            services1.AddRagNet(b => b.UseSqlitePersistence(dbPath, "collection-A"));
            var sp1 = services1.BuildServiceProvider();
            var ingestor1 = sp1.GetRequiredService<IIngestor>();
            await ingestor1.IngestAsync(
                new MemoryStream(System.Text.Encoding.UTF8.GetBytes("hello world")),
                new Rag.NET.Models.DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt" },
                cancellationToken: ct);
            await sp1.DisposeAsync();

            // --- Session 2: open with collection-B → stale guard should wipe ---
            var services2 = new ServiceCollection();
            services2.AddSingleton(vectorStore);
            services2.AddSingleton(embedder);
            services2.AddRagNet(b => b.UseSqlitePersistence(dbPath, "collection-B"));
            var sp2 = services2.BuildServiceProvider();

            var bm25 = sp2.GetRequiredService<IBm25Index>();
            var results = bm25.Search("hello", topK: 5);

            Assert.Empty(results); // stale guard should have wiped collection-A data
            await sp2.DisposeAsync();
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void AddRagNet_WithContentHashRecordManager_RegistersIContentHashStore()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ragnet-di-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(Substitute.For<IVectorStore>());
            services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
            services.AddRagNet(b => b.UseContentHashRecordManager(dbPath));

            using var sp = services.BuildServiceProvider();
            var store = sp.GetService<IContentHashStore>();
            Assert.NotNull(store);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task AddRagNet_WithParentDocumentRetrieval_OptedOut_ReturnsChildText()
    {
        var services = new ServiceCollection();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        services.AddSingleton(vectorStore);
        services.AddSingleton(embedder);
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new()
                {
                    Chunk = new TextChunk
                    {
                        Text = "small child", DocumentId = "doc1", ChunkIndex = 0,
                        Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["_parentKey"] = "doc1:0" }
                    },
                    Score = 0.9
                }
            });

        services.AddRagNet(b => b.UseParentDocumentRetrieval());

        var sp = services.BuildServiceProvider();
        var parentStore = sp.GetRequiredService<Rag.NET.Abstractions.IParentChunkStore>();
        parentStore.Add("doc1", 0, "large parent context text");

        var pipeline = sp.GetRequiredService<IRagPipeline>();
        var opts = new RetrievalOptions { UseParentDocument = false };
        var results = await pipeline.RetrieveAsync("query", opts, TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("small child", results[0].Chunk.Text);
    }
}
