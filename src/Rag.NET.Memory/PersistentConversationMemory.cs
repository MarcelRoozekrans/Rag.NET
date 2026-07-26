using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Resilience;

namespace Rag.NET.Memory;

public sealed class PersistentConversationMemory(
    IConversationMemory inner,
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    PersistentMemoryOptions options,
    ILogger<PersistentConversationMemory>? logger = null) : IConversationMemory
{
    // ChunkIndex is tracked per-session for this process lifetime.
    // The counter resets on restart; index collisions are possible for
    // sessions with existing entries in the vector store.
    private readonly ConcurrentDictionary<SessionId, int> _sessionCounters = new();

    // 0 until the opaque-scale warning has been emitted; flipped once via Interlocked so
    // concurrent recalls on the same instance still warn exactly once.
    private int _opaqueScaleWarned;

    public async Task<IReadOnlyList<ChatMessage>> ProcessAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = history.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
        if (!string.IsNullOrEmpty(query))
        {
            var matches = await SearchAsync(query, cancellationToken).ConfigureAwait(false);
            var relevant = SelectRelevant(matches);
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

    /// <summary>
    /// Narrows the store's matches to the exchanges worth injecting, honoring the store's
    /// declared <see cref="ScoreScale"/>.
    /// <para>
    /// On a similarity scale (the default for any store that does not implement
    /// <see cref="IScoreScaleAware"/>) this filters by
    /// <see cref="PersistentMemoryOptions.MinScore"/>, exactly as before.
    /// </para>
    /// <para>
    /// On <see cref="ScoreScale.OpaqueRanking"/> the scores are ordinal only — a Reciprocal
    /// Rank Fusion score peaks near 0.033 for two federated stores, so any similarity-calibrated
    /// threshold would discard every match. The threshold is skipped and the store's ranking is
    /// taken as-is, capped at <see cref="PersistentMemoryOptions.TopK"/>; the ignored option is
    /// warned about once per instance.
    /// </para>
    /// </summary>
    private IReadOnlyList<SearchResult> SelectRelevant(IReadOnlyList<SearchResult> matches)
    {
        if (vectorStore is not IScoreScaleAware { ScoreScale: ScoreScale.OpaqueRanking })
            return matches.Where(r => r.Score >= options.MinScore).ToList();

        WarnOpaqueScaleOnce();
        // The store already ordered by rank and applied TopK; the cap is belt-and-braces
        // for stores that return more than they were asked for.
        return matches.Count <= options.TopK ? matches : matches.Take(options.TopK).ToList();
    }

    private void WarnOpaqueScaleOnce()
    {
        if (logger is null || Interlocked.Exchange(ref _opaqueScaleWarned, 1) != 0) return;
        ConversationMemoryLog.OpaqueScoreScaleIgnoresMinScore(
            logger, DescribeStore(vectorStore), options.MinScore, options.TopK);
    }

    /// <summary>
    /// Names the store responsible for the opaque scale, seeing past
    /// <see cref="ResilientVectorStore"/>. <c>ConfigureResilience</c> decorates the registered
    /// <see cref="IVectorStore"/>, so a plain <c>GetType().Name</c> would report
    /// <c>"ResilientVectorStore"</c> for every decorated graph — and naming the real store is
    /// the whole point of the warning. The decorator delegates
    /// <see cref="IScoreScaleAware.ScoreScale"/>, so it is the inner store's scale that put us
    /// here. Decoration never stacks, so a single unwrap suffices.
    /// </summary>
    private static string DescribeStore(IVectorStore store) =>
        store is ResilientVectorStore resilient
            ? resilient.InnerStoreType.Name
            : store.GetType().Name;

    public async Task StoreAsync(
        string userMessage,
        string assistantMessage,
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(sessionId);
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
                    DocumentId = new DocumentId(sessionId.Value),
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
