using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Memory;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;
using SearchOptions = Rag.NET.Models.Options.SearchOptions;

namespace Rag.NET.Tests.Memory;

/// <summary>
/// Recall must respect the store's declared <see cref="ScoreScale"/>: threshold on a
/// similarity scale, rank-only on an opaque one. Hand-written fakes throughout — EPS06
/// forbids substituting <see cref="ValueTask"/> members, and the once-ness assertion needs a
/// logger that counts every entry rather than one that merely matches.
/// </summary>
public class PersistentConversationMemoryScoreScaleTests
{
    // ── Fakes ────────────────────────────────────────────────────────────────

    /// <summary>Similarity-scaled store: returns scripted results, declares no score scale.</summary>
    private sealed class ScriptedVectorStore(params SearchResult[] results) : IVectorStore
    {
        private int _searchCount;

        public int SearchCount => Volatile.Read(ref _searchCount);

        public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding, SearchOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _searchCount);
            return Task.FromResult<IReadOnlyList<SearchResult>>(results);
        }

        public Task DeleteByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>Same scripted behavior, but declares RRF-style opaque ranking scores.</summary>
    private sealed class OpaqueVectorStore(params SearchResult[] results) : IVectorStore, IScoreScaleAware
    {
        private int _searchCount;

        public int SearchCount => Volatile.Read(ref _searchCount);

        public ScoreScale ScoreScale => ScoreScale.OpaqueRanking;

        public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding, SearchOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _searchCount);
            return Task.FromResult<IReadOnlyList<SearchResult>>(results);
        }

        public Task DeleteByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>Counts every entry, so once-ness can be asserted rather than mere presence.</summary>
    private sealed class CountingLogger : ILogger<PersistentConversationMemory>
    {
        private readonly Lock _gate = new();
        private readonly List<(LogLevel Level, string? EventName, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string? EventName, string Message)> Entries
        {
            get { lock (_gate) return [.. _entries]; }
        }

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var entry = (logLevel, eventId.Name, formatter(state, exception));
            lock (_gate) _entries.Add(entry);
        }
    }

    private sealed class PassthroughMemory : IConversationMemory
    {
        public Task<IReadOnlyList<ChatMessage>> ProcessAsync(
            IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken = default) =>
            Task.FromResult(history);

        public Task StoreAsync(
            string userMessage, string assistantMessage, SessionId sessionId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ConstantEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var embeddings = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var _ in values) embeddings.Add(new Embedding<float>(new float[] { 0.1f }));
            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Source-generated <c>LoggerMessage</c> names the event after the method.</summary>
    private const string OpaqueWarningEventName = "OpaqueScoreScaleIgnoresMinScore";

    private static SearchResult Match(string text, double score) =>
        new()
        {
            Chunk = new TextChunk { Text = text, DocumentId = new DocumentId("s1"), ChunkIndex = 0 },
            Score = score,
        };

    private static PersistentConversationMemory Memory(
        IVectorStore store,
        PersistentMemoryOptions? options = null,
        ILogger<PersistentConversationMemory>? logger = null) =>
        new(new PassthroughMemory(), store, new ConstantEmbedder(),
            options ?? new PersistentMemoryOptions(), logger);

    private static IReadOnlyList<ChatMessage> Ask() => [new ChatMessage(ChatRole.User, "what did we discuss?")];

    /// <summary>The injected recall prefix, or <c>null</c> when nothing was recalled.</summary>
    private static string? RecalledPrefix(IReadOnlyList<ChatMessage> result) =>
        result.Count > 0 && result[0].Role == ChatRole.System ? result[0].Text : null;

    private static int WarningCount(CountingLogger logger)
    {
        var count = 0;
        foreach (var entry in logger.Entries)
        {
            if (string.Equals(entry.EventName, OpaqueWarningEventName, StringComparison.Ordinal)) count++;
        }
        return count;
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SimilarityStore_AppliesMinScore()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new ScriptedVectorStore(Match("above", 0.85), Match("below", 0.30));
        var sut = Memory(store, new PersistentMemoryOptions { MinScore = 0.7 });

        var prefix = RecalledPrefix(await sut.ProcessAsync(Ask(), ct));

        Assert.NotNull(prefix);
        Assert.Contains("above", prefix, StringComparison.Ordinal);
        Assert.DoesNotContain("below", prefix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SimilarityStore_AllBelowMinScore_RecallsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new ScriptedVectorStore(Match("faint", 0.30));
        var sut = Memory(store, new PersistentMemoryOptions { MinScore = 0.7 });

        Assert.Null(RecalledPrefix(await sut.ProcessAsync(Ask(), ct)));
    }

    [Fact]
    public async Task OpaqueStore_SkipsMinScore_RecallsTopK()
    {
        var ct = TestContext.Current.CancellationToken;
        // RRF scores as a two-store federation produces them: ~0.033 at best, so the
        // default MinScore of 0.7 would discard every one of them.
        var store = new OpaqueVectorStore(
            Match("first", 0.0328), Match("second", 0.0323), Match("third", 0.0161));
        var sut = Memory(store, new PersistentMemoryOptions { MinScore = 0.7, TopK = 2 });

        var prefix = RecalledPrefix(await sut.ProcessAsync(Ask(), ct));

        Assert.NotNull(prefix);
        Assert.Contains("first", prefix, StringComparison.Ordinal);
        Assert.Contains("second", prefix, StringComparison.Ordinal);
        // TopK caps the rank-ordered list; the threshold plays no part in the selection.
        Assert.DoesNotContain("third", prefix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpaqueStore_WarnsOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CountingLogger();
        var store = new OpaqueVectorStore(Match("recalled", 0.0328));
        var sut = Memory(store, new PersistentMemoryOptions { MinScore = 0.7, TopK = 3 }, logger);

        var first = RecalledPrefix(await sut.ProcessAsync(Ask(), ct));
        var second = RecalledPrefix(await sut.ProcessAsync(Ask(), ct));

        // Both recalls genuinely ran the opaque path — otherwise "warned once" could be
        // satisfied by a second call that searched nothing and recalled nothing.
        Assert.Equal(2, store.SearchCount);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Contains("recalled", second, StringComparison.Ordinal);

        // Exactly one warning across both recalls, and nothing else was logged at all.
        var entries = logger.Entries;
        Assert.Equal(1, WarningCount(logger));
        Assert.Single(entries);
        Assert.Equal(LogLevel.Warning, entries[0].Level);

        // The warning names the store type and the option it is ignoring.
        Assert.Contains(nameof(OpaqueVectorStore), entries[0].Message, StringComparison.Ordinal);
        Assert.Contains("MinScore", entries[0].Message, StringComparison.Ordinal);
        Assert.Contains("0.7", entries[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpaqueStore_ManyRecalls_StillWarnsOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CountingLogger();
        var store = new OpaqueVectorStore(Match("recalled", 0.0328));
        var sut = Memory(store, new PersistentMemoryOptions(), logger);

        // The fakes complete synchronously, so these recalls do not actually run in
        // parallel — this pins repeated invocation, not thread interleaving. The
        // once-flag's thread safety rests on Interlocked, which is not under test here.
        var recalls = new List<Task<IReadOnlyList<ChatMessage>>>(8);
        while (recalls.Count < 8) recalls.Add(sut.ProcessAsync(Ask(), ct));
        await Task.WhenAll(recalls);

        Assert.Equal(recalls.Count, store.SearchCount);
        Assert.Equal(1, WarningCount(logger));
    }

    [Fact]
    public async Task StoreWithoutCapability_BehavesAsSimilarity()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CountingLogger();
        var store = new ScriptedVectorStore(Match("kept", 0.9), Match("dropped", 0.1));
        Assert.IsNotAssignableFrom<IScoreScaleAware>(store);

        var sut = Memory(store, new PersistentMemoryOptions { MinScore = 0.7 }, logger);
        var prefix = RecalledPrefix(await sut.ProcessAsync(Ask(), ct));

        Assert.NotNull(prefix);
        Assert.Contains("kept", prefix, StringComparison.Ordinal);
        Assert.DoesNotContain("dropped", prefix, StringComparison.Ordinal);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void FederatedVectorStore_DeclaresOpaqueRanking_PlainStoreDeclaresNothing()
    {
        var federated = new FederatedVectorStore(
            [new ScriptedVectorStore(), new ScriptedVectorStore()],
            new FederatedStoreOptions());

        var scaleAware = Assert.IsAssignableFrom<IScoreScaleAware>(federated);
        Assert.Equal(ScoreScale.OpaqueRanking, scaleAware.ScoreScale);

        using var similarityStore = new InMemoryVectorStore();
        Assert.IsNotAssignableFrom<IScoreScaleAware>(similarityStore);
    }
}
