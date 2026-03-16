using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
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
    InMemoryBm25Index bm25Index,
    ILogger? logger = null) : IRetriever
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

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

        IReadOnlyList<SearchResult> results;
        string searchMode;

        if (opts.UseHybridSearch)
        {
            if (vectorStore is IHybridSearchable hybrid)
            {
                searchMode = "hybrid-native";
                results = await hybrid.HybridSearchAsync(query, queryEmbeddings[0].Vector, searchOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                searchMode = "hybrid-bm25-fallback";
                var denseTask = vectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, cancellationToken);
                var bm25Hits = bm25Index.Search(query, topK: searchOptions.TopK);
                var dense = await denseTask.ConfigureAwait(false);
                results = RrfMerger.Merge(dense, bm25Hits, searchOptions.TopK);
            }
        }
        else
        {
            searchMode = "dense";
            results = await vectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        RagPipelineLog.VectorStoreSearchCompleted(_logger, searchMode, results.Count);
        return results;
    }
}
