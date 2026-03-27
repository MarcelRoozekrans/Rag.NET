using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Memory;

public sealed class PersistentConversationMemory(
    IConversationMemory inner,
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    PersistentMemoryOptions options,
    ILogger<PersistentConversationMemory>? logger = null) : IConversationMemory
{
    private readonly ConcurrentDictionary<string, int> _sessionCounters = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<ChatMessage>> ProcessAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        var query = history.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
        if (!string.IsNullOrEmpty(query))
        {
            var matches = await SearchAsync(query, cancellationToken).ConfigureAwait(false);
            var relevant = matches.Where(r => r.Score >= options.MinScore).ToList();
            if (relevant.Count > 0)
            {
                var prefix = "From a previous conversation:\n" +
                    string.Join("\n", relevant.Select(r => r.Chunk.Text));
                var withPrefix = new List<ChatMessage>(history.Count + 1) { new(ChatRole.System, prefix) };
                withPrefix.AddRange(history);
                return await inner.ProcessAsync(withPrefix, cancellationToken).ConfigureAwait(false);
            }
        }
        return await inner.ProcessAsync(history, cancellationToken).ConfigureAwait(false);
    }

    public async Task StoreAsync(
        string userMessage,
        string assistantMessage,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var text = $"User: {userMessage}\nAssistant: {assistantMessage}";
        try
        {
            var embeddings = await embedder
                .GenerateAsync([text], cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var chunkIndex = _sessionCounters.AddOrUpdate(sessionId, 0, (_, v) => v + 1);
            var chunk = new EmbeddedChunk
            {
                Chunk = new TextChunk
                {
                    Text = text,
                    DocumentId = new DocumentId(sessionId),
                    ChunkIndex = chunkIndex,
                },
                Embedding = embeddings[0].Vector,
            };
            await vectorStore.StoreAsync([chunk], cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to persist exchange for session '{SessionId}'; exchange not stored", sessionId);
        }
    }

    private async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, CancellationToken cancellationToken)
    {
        try
        {
            var embeddings = await embedder
                .GenerateAsync([query], cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return await vectorStore
                .SearchAsync(embeddings[0].Vector, new SearchOptions { TopK = options.TopK }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Persistent memory search failed for query '{Query}'; skipping prefix injection", query);
            return [];
        }
    }
}
