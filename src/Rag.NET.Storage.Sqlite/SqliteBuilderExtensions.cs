using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rag.NET.Abstractions;
using Rag.NET.Models.Options;
using Rag.NET.Search;
using Rag.NET.Storage;

namespace Rag.NET.DependencyInjection;

/// <summary>
/// SQLite-backed registration extensions for the RAG builder. These methods (and the stores
/// they register) lived in the core <c>Rag.NET</c> package before the package decomposition;
/// they keep their namespaces so existing consumer source compiles unchanged after adding a
/// reference to <c>Rag.NET.Storage.Sqlite</c>.
/// </summary>
public static class SqliteBuilderExtensions
{
    /// <summary>
    /// Registers SQLite-backed persistence for <see cref="IBm25Index"/> and <see cref="IParentChunkStore"/>.
    /// On startup, both stores load persisted data from <paramref name="dbPath"/>.
    /// Every Add/Remove writes through to SQLite synchronously.
    /// </summary>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="dbPath">Path to the SQLite database file. Created if it does not exist.</param>
    /// <param name="collectionName">
    /// Optional stale-data guard. If the registered name differs from what is stored in the database,
    /// all persisted data is wiped before loading. Change this value when replacing the vector store.
    /// Omit to skip the stale guard.
    /// </param>
    public static TBuilder UseSqlitePersistence<TBuilder>(
        this TBuilder builder,
        string dbPath,
        string? collectionName = null)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<SqliteBm25Index>(
            sp => new SqliteBm25Index(dbPath, collectionName, sp.GetService<SynonymMap>()));
        builder.Services.AddSingleton<IBm25Index>(sp => sp.GetRequiredService<SqliteBm25Index>());

        builder.Services.AddSingleton<SqliteParentChunkStore>(_ => new SqliteParentChunkStore(dbPath, collectionName));
        builder.Services.AddSingleton<IParentChunkStore>(sp => sp.GetRequiredService<SqliteParentChunkStore>());

        return builder;
    }

    /// <summary>
    /// Registers <see cref="SqliteContentHashStore"/> as the <see cref="IContentHashStore"/>.
    /// When registered, <see cref="RagPipelineExtensions.IngestFromProviderAsync"/> automatically skips
    /// files that have not changed since the last ingestion run.
    /// </summary>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="dbPath">Path to the SQLite file. Created if it does not exist.</param>
    public static TBuilder UseContentHashRecordManager<TBuilder>(this TBuilder builder, string dbPath)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<IContentHashStore>(_ => new SqliteContentHashStore(dbPath));
        return builder;
    }

    /// <summary>
    /// Registers the SQLite-backed <see cref="IEmbeddingVersionStore"/>
    /// (<see cref="SqliteEmbeddingVersionStore"/>) so the ingestion pipeline stamps each
    /// document with the embedding model that produced its vectors, and
    /// <c>ReindexStaleAsync</c> can find documents embedded by an older model. The model
    /// identity comes from the generator's <c>EmbeddingGeneratorMetadata</c> or the
    /// explicit <see cref="EmbeddingVersioningOptions.ModelId"/> override; when neither
    /// is available, stamping is disabled with a one-time warning. Idempotent: options
    /// and store use <c>TryAdd</c>, so the first registration wins.
    /// </summary>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="configure">Configures the <see cref="EmbeddingVersioningOptions"/>.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <see cref="EmbeddingVersioningOptions.DatabasePath"/> is empty.
    /// </exception>
    public static TBuilder UseEmbeddingVersioning<TBuilder>(
        this TBuilder builder,
        Action<EmbeddingVersioningOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new EmbeddingVersioningOptions();
        configure?.Invoke(opts);

        if (string.IsNullOrWhiteSpace(opts.DatabasePath))
        {
            throw new ArgumentException(
                "EmbeddingVersioningOptions.DatabasePath must be a non-empty string.",
                nameof(configure));
        }

        builder.Services.TryAddSingleton(opts);
        builder.Services.TryAddSingleton<IEmbeddingVersionStore>(
            sp => new SqliteEmbeddingVersionStore(sp.GetRequiredService<EmbeddingVersioningOptions>().DatabasePath));
        return builder;
    }

    /// <summary>
    /// Registers <see cref="SqliteCostLedger"/> as the <see cref="ICostLedger"/> so daily and
    /// monthly spend recorded by <c>UseCostBudgeting</c> survives process restarts. Call this
    /// <em>before</em> <c>UseCostBudgeting</c>: the ledger is registered with <c>TryAdd</c>
    /// (first-wins, the same convention as <c>UseEmbeddingVersioning</c>), and
    /// <c>UseCostBudgeting</c> only falls back to its in-memory default when no
    /// <see cref="ICostLedger"/> is registered yet.
    /// </summary>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="dbPath">
    /// Path of the SQLite cost-ledger database file. Created if it does not exist.
    /// Defaults to <c>rag-cost-ledger.db</c>. This parameter is the only place the ledger
    /// path is configured — <c>CostBudgetOptions</c> has no path property of its own.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="dbPath"/> is empty.</exception>
    public static TBuilder UseSqliteCostLedger<TBuilder>(
        this TBuilder builder,
        string dbPath = "rag-cost-ledger.db")
        where TBuilder : IRagBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        builder.Services.TryAddSingleton<ICostLedger>(sp =>
            new SqliteCostLedger(dbPath, sp.GetService<TimeProvider>() ?? TimeProvider.System));
        return builder;
    }
}
