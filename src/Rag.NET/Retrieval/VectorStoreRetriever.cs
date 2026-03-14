using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Search;

namespace Rag.NET.Retrieval;

/// <summary>
/// Base retriever that embeds the query and searches the vector store.
/// Handles hybrid search via <see cref="IHybridSearchable"/> or BM25 fallback + RRF merge.
/// </summary>
public sealed class VectorStoreRetriever(
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    InMemoryBm25Index bm25Index) : IRetriever
{
    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RetrievalOptions();

        var searchOptions = new SearchOptions
        {
            TopK = opts.TopK,
            MinScore = opts.MinScore,
            MetadataFilter = opts.MetadataFilter,
            UseHybridSearch = opts.UseHybridSearch,
        };

        var textToEmbed = opts.EmbeddingTextOverride ?? query;
        var queryEmbeddings = await embeddingGenerator.GenerateAsync(
            [textToEmbed], cancellationToken: cancellationToken).ConfigureAwait(false);

        if (opts.UseHybridSearch)
        {
            if (vectorStore is IHybridSearchable hybrid)
            {
                return await hybrid.HybridSearchAsync(query, queryEmbeddings[0].Vector, searchOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            var denseTask = vectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, cancellationToken);
            var bm25Hits = bm25Index.Search(query, topK: searchOptions.TopK);
            var dense = await denseTask.ConfigureAwait(false);
            return RrfMerger.Merge(dense, bm25Hits, searchOptions.TopK);
        }

        return await vectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, cancellationToken)
            .ConfigureAwait(false);
    }
}
