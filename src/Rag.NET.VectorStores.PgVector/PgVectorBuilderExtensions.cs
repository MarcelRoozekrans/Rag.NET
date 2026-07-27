using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.PgVector;

public static class PgVectorBuilderExtensions
{
    /// <param name="builder">The RAG builder.</param>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="vectorDimensions">Dense embedding dimensions.</param>
    /// <param name="enableSparseVectors">
    /// When <see langword="true"/>, registers <see cref="PgVectorSparseVectorStore"/> instead:
    /// <c>InitializeAsync</c> adds a <c>sparse_embedding sparsevec</c> column with its own HNSW
    /// index and the store serves <c>ISparseSearchable</c> — pair with <c>UseSpladeEncoder</c>
    /// for SPLADE hybrid retrieval. Requires pgvector 0.7.0 or later, which
    /// <c>InitializeAsync</c> verifies.
    /// </param>
    /// <param name="sparseVocabularySize">
    /// Dimension of the <c>sparsevec</c> column when <paramref name="enableSparseVectors"/> is
    /// set: the sparse encoder's vocabulary size, which must exceed every term id it emits.
    /// Ignored otherwise.
    /// </param>
    public static TBuilder UsePgVector<TBuilder>(
        this TBuilder builder,
        string connectionString,
        int vectorDimensions = 1536,
        bool enableSparseVectors = false,
        int sparseVocabularySize = PgVectorSparseVectorStore.DefaultSparseVocabularySize)
        where TBuilder : IRagBuilder
    {
        PgVectorStore store = enableSparseVectors
            ? new PgVectorSparseVectorStore(connectionString, vectorDimensions, sparseVocabularySize)
            : new PgVectorStore(connectionString, vectorDimensions);
        builder.Services.AddSingleton<IVectorStore>(store);
        builder.Services.AddSingleton<ICollectionManageable>(store);
        return builder;
    }
}
