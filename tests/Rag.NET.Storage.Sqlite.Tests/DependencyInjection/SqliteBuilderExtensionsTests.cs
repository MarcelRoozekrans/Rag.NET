using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public sealed class SqliteBuilderExtensionsTests
{
    // ── UseSqlitePersistence / UseContentHashRecordManager ───────────────────
    // Moved from Rag.NET.Tests' ServiceCollectionExtensionsTests when the SQLite
    // stores were extracted from core into Rag.NET.Storage.Sqlite.

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
            _ = await ingestor1.IngestAsync(
                new MemoryStream(System.Text.Encoding.UTF8.GetBytes("hello world")),
                new Rag.NET.Models.DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "test.txt" },
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
            _ = await ingestor1.IngestAsync(
                new MemoryStream(System.Text.Encoding.UTF8.GetBytes("hello world")),
                new Rag.NET.Models.DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "test.txt" },
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

    // ── UseSqliteCostLedger ──────────────────────────────────────────────────

    [Fact]
    public void UseSqliteCostLedger_RegistersSqliteLedgerAtConfiguredPath()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ragnet-sqliteledger-{Guid.NewGuid():N}.db");
        try
        {
            using var sp = new ServiceCollection()
                .AddRagNet(rag => rag.UseSqliteCostLedger(dbPath))
                .BuildServiceProvider();

            Assert.IsType<SqliteCostLedger>(sp.GetRequiredService<ICostLedger>());
            Assert.True(File.Exists(dbPath));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void UseSqliteCostLedger_BeforeUseCostBudgeting_SqliteLedgerGatesTheBudget()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ragnet-sqliteledger-{Guid.NewGuid():N}.db");
        try
        {
            // The migration path for the pre-decomposition default: the SQLite ledger is
            // registered first, so UseCostBudgeting's in-memory TryAdd fallback never lands.
            using var sp = new ServiceCollection()
                .AddRagNet(rag =>
                {
                    rag.Services.AddSingleton(Substitute.For<IChatClient>());
                    rag.UseSqliteCostLedger(dbPath);
                    rag.UseCostBudgeting(o => o.DailyLimit = 10m);
                })
                .BuildServiceProvider();

            Assert.IsType<SqliteCostLedger>(sp.GetRequiredService<ICostLedger>());
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void UseSqliteCostLedger_CustomLedgerRegisteredFirst_Wins()
    {
        var custom = Substitute.For<ICostLedger>();
        using var sp = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.Services.AddSingleton(custom);
                rag.UseSqliteCostLedger(Path.Combine(Path.GetTempPath(), $"ragnet-unused-{Guid.NewGuid():N}.db"));
            })
            .BuildServiceProvider();

        Assert.Same(custom, sp.GetRequiredService<ICostLedger>());
    }

    [Fact]
    public void UseSqliteCostLedger_BlankPath_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseSqliteCostLedger(" ")));
    }
}
