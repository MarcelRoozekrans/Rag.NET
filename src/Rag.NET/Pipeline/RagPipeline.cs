using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
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
    ResiliencePipeline? resiliencePipeline = null) : IRagPipeline
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly ResiliencePipeline? _resiliencePipeline = resiliencePipeline;

    private const string DefaultSystemPrompt =
        "Answer the user's question based only on the provided context. " +
        "If the context doesn't contain enough information, say so. " +
        "Cite which sources you used.";

    public async Task<IngestionResult> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var parser = parsers.FirstOrDefault(p => p.CanParse(metadata.ContentType ?? "text/plain"))
            ?? throw new InvalidOperationException(
                $"No parser registered for content type '{metadata.ContentType}'.");

        if (options?.Overwrite == true)
        {
            await vectorStore.DeleteByDocumentIdAsync(metadata.DocumentId, cancellationToken).ConfigureAwait(false);
        }

        var chunks = new List<TextChunk>();

        await foreach (var section in parser.ParseAsync(document, metadata, cancellationToken).ConfigureAwait(false))
        {
            await foreach (var chunk in chunkingStrategy.ChunkAsync(section, chunkingOptions, cancellationToken).ConfigureAwait(false))
            {
                chunks.Add(chunk);
            }
        }

        foreach (ref var chunk in CollectionsMarshal.AsSpan(chunks))
        {
            foreach (var tag in metadata.Tags)
            {
                chunk.Metadata.TryAdd(tag.Key, tag.Value);
            }
            chunk.Metadata.TryAdd("document_id", metadata.DocumentId);
            chunk.Metadata.TryAdd("file_name", metadata.FileName);
        }

        if (chunks.Count == 0)
        {
            return new IngestionResult { DocumentId = metadata.DocumentId, ChunksStored = 0 };
        }

        var texts = chunks.Select(c => c.Text).ToList();
        var embeddings = await embeddingGenerator.GenerateAsync(texts, cancellationToken: cancellationToken).ConfigureAwait(false);

        var embeddedChunks = chunks
            .Zip(embeddings, (chunk, embedding) => new EmbeddedChunk
            {
                Chunk = chunk,
                Embedding = embedding.Vector,
            })
            .ToList();

        await vectorStore.StoreAsync(embeddedChunks, cancellationToken).ConfigureAwait(false);

        return new IngestionResult { DocumentId = metadata.DocumentId, ChunksStored = embeddedChunks.Count };
    }

    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RetrievalOptions();
        var queryEmbeddings = await embeddingGenerator.GenerateAsync(
            [query], cancellationToken: cancellationToken).ConfigureAwait(false);

        var searchOptions = new SearchOptions
        {
            TopK = opts.TopK,
            MinScore = opts.MinScore,
            MetadataFilter = opts.MetadataFilter,
            UseHybridSearch = opts.UseHybridSearch,
        };

        if (opts.UseHybridSearch)
        {
            if (vectorStore is not IHybridSearchable hybrid)
            {
                throw new InvalidOperationException(
                    "The registered IVectorStore does not implement IHybridSearchable. " +
                    "Use a vector store that supports hybrid search, such as AzureAISearchVectorStore.");
            }

            return await hybrid.HybridSearchAsync(query, queryEmbeddings[0].Vector, searchOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        return await vectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, cancellationToken).ConfigureAwait(false);
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

    public Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
    {
        return vectorStore.DeleteByDocumentIdAsync(documentId, cancellationToken);
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
}
