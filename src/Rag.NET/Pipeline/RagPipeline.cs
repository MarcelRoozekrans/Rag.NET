using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Pipeline;

public sealed class RagPipeline(
    IEnumerable<IDocumentParser> parsers,
    IChunkingStrategy chunkingStrategy,
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IChatClient? chatClient,
    ChunkingOptions chunkingOptions) : IRagPipeline
{
    private const string DefaultSystemPrompt =
        "Answer the user's question based only on the provided context. " +
        "If the context doesn't contain enough information, say so. " +
        "Cite which sources you used.";

    public async Task<IngestionResult> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        var parser = parsers.FirstOrDefault(p => p.CanParse(metadata.ContentType ?? "text/plain"))
            ?? throw new InvalidOperationException(
                $"No parser registered for content type '{metadata.ContentType}'.");

        var chunks = new List<TextChunk>();

        await foreach (var section in parser.ParseAsync(document, metadata, cancellationToken).ConfigureAwait(false))
        {
            await foreach (var chunk in chunkingStrategy.ChunkAsync(section, chunkingOptions, cancellationToken).ConfigureAwait(false))
            {
                chunks.Add(chunk);
            }
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
        };

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
        var retrievalOptions = new RetrievalOptions { TopK = opts.TopK, MinScore = opts.MinScore };
        var sources = await RetrieveAsync(query, retrievalOptions, cancellationToken).ConfigureAwait(false);

        var context = string.Join("\n\n---\n\n",
            sources.Select((s, i) => $"[Source {i + 1}]\n{s.Chunk.Text}"));

        var systemPrompt = opts.SystemPrompt ?? DefaultSystemPrompt;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, $"Context:\n{context}\n\nQuestion: {query}"),
        };

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
        var retrievalOptions = new RetrievalOptions { TopK = opts.TopK, MinScore = opts.MinScore };
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
            new(ChatRole.User, $"Context:\n{context}\n\nQuestion: {query}"),
        };

        var chatOptions = new ChatOptions();
        if (opts.Temperature.HasValue)
        {
            chatOptions.Temperature = opts.Temperature.Value;
        }

        return (messages, chatOptions);
    }
}
