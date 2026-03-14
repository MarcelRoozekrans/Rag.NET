using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.PostRetrieval;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Search;
using Rag.NET.Telemetry;

namespace Rag.NET.Pipeline;

public sealed class RagPipeline(
    IEnumerable<IDocumentParser> parsers,
    IChunkingStrategy chunkingStrategy,
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IChatClient? chatClient,
    ChunkingOptions chunkingOptions,
    ILogger<RagPipeline>? logger = null,
    ResiliencePipeline? resiliencePipeline = null,
    IQueryExpander? queryExpander = null,
    MultiQueryOptions? multiQueryOptions = null,
    IReranker? reranker = null) : IRagPipeline, IDisposable
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly ResiliencePipeline? _resiliencePipeline = resiliencePipeline;
    private readonly IQueryExpander? _queryExpander = queryExpander;
    private readonly MultiQueryOptions _multiQueryOptions = multiQueryOptions ?? new MultiQueryOptions();
    private readonly IReranker? _reranker = reranker;
    private readonly InMemoryBm25Index _bm25Index = new();
    private int _nextBm25DocId;

    private const string DefaultSystemPrompt =
        "Answer the user's question based only on the provided context. " +
        "If the context doesn't contain enough information, say so. " +
        "Cite which sources you used.";

    /// <remarks>
    /// Concurrent ingests of the same <paramref name="documentId"/> are not supported.
    /// Callers must ensure sequential ingestion per document; the BM25 index update and
    /// the vector store write are not transactional.
    /// </remarks>
    public async Task<IngestionResult> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var parser = parsers.FirstOrDefault(p => p.CanParse(metadata.ContentType ?? "text/plain"))
            ?? throw new InvalidOperationException(
                $"No parser registered for content type '{metadata.ContentType}'.");

        if (options?.Overwrite == true)
        {
            await vectorStore.DeleteByDocumentIdAsync(metadata.DocumentId, cancellationToken).ConfigureAwait(false);
            _bm25Index.Remove(metadata.DocumentId);
        }

        var chunks = await ParseAndChunkAsync(parser, document, metadata, cancellationToken).ConfigureAwait(false);

        ReportProgress(progress, IngestionProgressStage.Parsing, metadata.DocumentId, null, null, "Parsing complete");
        ApplyMetadataTags(chunks, metadata);
        ReportProgress(progress, IngestionProgressStage.Chunking, metadata.DocumentId, chunks.Count, chunks.Count, $"Chunked into {chunks.Count} chunks");

        if (chunks.Count == 0)
            return new IngestionResult { DocumentId = metadata.DocumentId, ChunksStored = 0 };

        var texts = chunks.Select(c => c.Text).ToList();
        var embeddings = await embeddingGenerator.GenerateAsync(texts, cancellationToken: cancellationToken).ConfigureAwait(false);

        var embeddedChunks = chunks
            .Zip(embeddings, (chunk, embedding) => new EmbeddedChunk { Chunk = chunk, Embedding = embedding.Vector })
            .ToList();

        ReportProgress(progress, IngestionProgressStage.Embedding, metadata.DocumentId, embeddedChunks.Count, embeddedChunks.Count, $"Generated {embeddedChunks.Count} embeddings");
        await vectorStore.StoreAsync(embeddedChunks, cancellationToken).ConfigureAwait(false);
        ReportProgress(progress, IngestionProgressStage.Storing, metadata.DocumentId, embeddedChunks.Count, embeddedChunks.Count, $"Stored {embeddedChunks.Count} chunks");

        foreach (ref readonly var ec in CollectionsMarshal.AsSpan(embeddedChunks))
        {
            var id = System.Threading.Interlocked.Increment(ref _nextBm25DocId);
            _bm25Index.Add(id, ec.Chunk);
        }

        return new IngestionResult { DocumentId = metadata.DocumentId, ChunksStored = embeddedChunks.Count };
    }

    private async Task<List<TextChunk>> ParseAndChunkAsync(
        IDocumentParser parser,
        Stream document,
        DocumentMetadata metadata,
        CancellationToken cancellationToken)
    {
        var chunks = new List<TextChunk>();
        var headingBreadcrumbs = new string?[6];

        await foreach (var section in parser.ParseAsync(document, metadata, cancellationToken).ConfigureAwait(false))
        {
            Dictionary<string, string>? headingMetadata = null;

            if (section.HeadingLevel is { } level && level >= 1 && level <= 6 && section.Heading is not null)
            {
                headingBreadcrumbs[level - 1] = section.Heading;
                var idx = level;
                while (idx < 6)
                {
                    headingBreadcrumbs[idx] = null;
                    idx++;
                }

                var parts = new List<string>(level);
                foreach (var h in headingBreadcrumbs[..level])
                {
                    if (h is not null)
                        parts.Add(h);
                }

                var breadcrumb = string.Join(" > ", parts);
                headingMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["heading"] = section.Heading,
                    ["heading_level"] = level.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["heading_breadcrumb"] = breadcrumb,
                };
            }

            await foreach (var chunk in chunkingStrategy.ChunkAsync(section, chunkingOptions, cancellationToken).ConfigureAwait(false))
            {
                if (headingMetadata is not null)
                {
                    foreach (var kv in headingMetadata)
                        chunk.Metadata.TryAdd(kv.Key, kv.Value);
                }

                chunks.Add(chunk);
            }
        }

        return chunks;
    }

    private static void ApplyMetadataTags(List<TextChunk> chunks, DocumentMetadata metadata)
    {
        foreach (ref var chunk in CollectionsMarshal.AsSpan(chunks))
        {
            foreach (var tag in metadata.Tags)
                chunk.Metadata.TryAdd(tag.Key, tag.Value);
            chunk.Metadata.TryAdd("document_id", metadata.DocumentId);
            chunk.Metadata.TryAdd("file_name", metadata.FileName);
        }
    }

    private static void ReportProgress(
        IProgress<IngestionProgress>? progress,
        IngestionProgressStage stage,
        string documentId,
        int? current,
        int? total,
        string message)
    {
        progress?.Report(new IngestionProgress
        {
            Stage = stage,
            DocumentId = documentId,
            Current = current,
            Total = total,
            Message = message,
        });
    }

    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RetrievalOptions();

        var searchOptions = new SearchOptions
        {
            TopK = (_reranker is not null && opts.UseReranking)
                ? (opts.CandidateCount ?? opts.TopK * 3)
                : opts.TopK,
            MinScore = opts.MinScore,
            MetadataFilter = opts.MetadataFilter,
            UseHybridSearch = opts.UseHybridSearch,
        };

        var searchResults = (_queryExpander is not null && opts.UseMultiQuery)
            ? await MultiQuerySearchAsync(query, searchOptions, opts, cancellationToken).ConfigureAwait(false)
            : await SearchSingleQueryAsync(query, searchOptions, opts.UseHybridSearch, cancellationToken).ConfigureAwait(false);

        if (opts.UseRedundancyFilter)
            searchResults = await RedundancyFilter.FilterAsync(searchResults, embeddingGenerator, opts.RedundancyThreshold, cancellationToken)
                .ConfigureAwait(false);

        if (_reranker is not null && opts.UseReranking)
            searchResults = await RerankAsync(query, searchResults, opts.TopK, cancellationToken).ConfigureAwait(false);

        if (opts.UseLostInTheMiddleReordering)
            searchResults = LostInTheMiddleReorderer.Reorder(searchResults);

        return searchResults;
    }

    private async Task<IReadOnlyList<SearchResult>> MultiQuerySearchAsync(
        string query,
        SearchOptions searchOptions,
        RetrievalOptions opts,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> variants;
        try
        {
            variants = await _queryExpander!.ExpandAsync(query, _multiQueryOptions.VariantCount, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RagPipelineLog.QueryExpansionFailed(_logger, query, ex);
            variants = [];
        }

        var allQueries = new List<string>(variants.Count + 1) { query };
        allQueries.AddRange(variants);

        var tasks = allQueries.Select(q => SearchSingleQueryAsync(q, searchOptions, opts.UseHybridSearch, cancellationToken)).ToArray();
        var allResults = await Task.WhenAll(tasks).ConfigureAwait(false);

        return allResults
            .SelectMany(r => r)
            .GroupBy(r => (r.Chunk.DocumentId, r.Chunk.ChunkIndex))
            .Select(g => g.MaxBy(r => r.Score)!)
            .OrderByDescending(r => r.Score)
            .Take(opts.TopK)
            .ToList()
            .AsReadOnly();
    }

    private async Task<IReadOnlyList<SearchResult>> RerankAsync(
        string query,
        IReadOnlyList<SearchResult> searchResults,
        int topK,
        CancellationToken cancellationToken)
    {
        try
        {
            var candidateCount = searchResults.Count;
            var reranked = await _reranker!.RerankAsync(query, searchResults, cancellationToken)
                .ConfigureAwait(false);

            var results = reranked
                .OrderByDescending(r => r.RelevanceScore)
                .Take(topK)
                .Select(r => r.SearchResult)
                .ToList()
                .AsReadOnly();

            RagPipelineLog.RerankingCompleted(_logger, candidateCount, results.Count);
            return results;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.RerankingFailed(_logger, query, ex);
            return searchResults;
        }
    }

    private async Task<IReadOnlyList<SearchResult>> SearchSingleQueryAsync(
        string query,
        SearchOptions searchOptions,
        bool useHybridSearch,
        CancellationToken cancellationToken)
    {
        var queryEmbeddings = await embeddingGenerator.GenerateAsync(
            [query], cancellationToken: cancellationToken).ConfigureAwait(false);

        if (useHybridSearch)
        {
            if (vectorStore is IHybridSearchable hybrid)
            {
                return await hybrid.HybridSearchAsync(query, queryEmbeddings[0].Vector, searchOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            var denseTask = vectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, cancellationToken);
            var bm25Hits = _bm25Index.Search(query, topK: searchOptions.TopK);
            var dense = await denseTask.ConfigureAwait(false);
            return RrfMerger.Merge(dense, bm25Hits, searchOptions.TopK);
        }

        return await vectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<RagResponse> AskAsync(
        string query,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (chatClient is null)
        {
            throw new InvalidOperationException(
                "IChatClient is not registered. Register an IChatClient in DI to use AskAsync.");
        }

        var opts = options ?? new RagOptions();
        var retrievalOptions = new RetrievalOptions
        {
            TopK = opts.TopK,
            MinScore = opts.MinScore,
            MetadataFilter = opts.MetadataFilter,
            UseHybridSearch = opts.UseHybridSearch,
            UseLostInTheMiddleReordering = opts.UseLostInTheMiddleReordering,
            UseRedundancyFilter = opts.UseRedundancyFilter,
            RedundancyThreshold = opts.RedundancyThreshold,
        };
        var sources = await RetrieveAsync(query, retrievalOptions, cancellationToken).ConfigureAwait(false);

        var context = string.Join("\n\n---\n\n",
            sources.Select((s, i) => $"[Source {i + 1}]\n{s.Chunk.Text}"));

        var systemPrompt = opts.SystemPrompt ?? DefaultSystemPrompt;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
        };

        if (opts.ConversationHistory is { Count: > 0 })
        {
            messages.AddRange(opts.ConversationHistory);
        }

        messages.Add(new ChatMessage(ChatRole.User, $"Context:\n{context}\n\nQuestion: {query}"));

        var chatOptions = new ChatOptions();
        if (opts.Temperature.HasValue)
        {
            chatOptions.Temperature = opts.Temperature.Value;
        }

        var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false);

        return new RagResponse
        {
            Answer = response.Text ?? string.Empty,
            Sources = sources,
        };
    }

    public async IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        RagOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (chatClient is null)
        {
            throw new InvalidOperationException(
                "IChatClient is not registered. Register an IChatClient in DI to use AskStreamingAsync.");
        }

        var opts = options ?? new RagOptions();
        var retrievalOptions = new RetrievalOptions
        {
            TopK = opts.TopK,
            MinScore = opts.MinScore,
            MetadataFilter = opts.MetadataFilter,
            UseHybridSearch = opts.UseHybridSearch,
            UseLostInTheMiddleReordering = opts.UseLostInTheMiddleReordering,
            UseRedundancyFilter = opts.UseRedundancyFilter,
            RedundancyThreshold = opts.RedundancyThreshold,
        };
        var sources = await RetrieveAsync(query, retrievalOptions, cancellationToken).ConfigureAwait(false);

        yield return new RagStreamingUpdate { Sources = sources };

        var (messages, chatOptions) = BuildRagMessages(sources, query, opts);

        await foreach (var update in chatClient.GetStreamingResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false))
        {
            if (update.Text is not null)
            {
                yield return new RagStreamingUpdate { TextDelta = update.Text };
            }
        }
    }

    public async Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await vectorStore.DeleteByDocumentIdAsync(documentId, cancellationToken).ConfigureAwait(false);
        _bm25Index.Remove(documentId);
    }

    private static (List<ChatMessage> Messages, ChatOptions Options) BuildRagMessages(
        IReadOnlyList<SearchResult> sources,
        string query,
        RagOptions opts)
    {
        var context = string.Join("\n\n---\n\n",
            sources.Select((s, i) => $"[Source {i + 1}]\n{s.Chunk.Text}"));

        var systemPrompt = opts.SystemPrompt ?? DefaultSystemPrompt;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
        };

        if (opts.ConversationHistory is { Count: > 0 })
        {
            messages.AddRange(opts.ConversationHistory);
        }

        messages.Add(new ChatMessage(ChatRole.User, $"Context:\n{context}\n\nQuestion: {query}"));

        var chatOptions = new ChatOptions();
        if (opts.Temperature.HasValue)
        {
            chatOptions.Temperature = opts.Temperature.Value;
        }

        return (messages, chatOptions);
    }

    public void Dispose() => _bm25Index.Dispose();
}
