using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Memory;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Memory.Tests;

/// <summary>
/// Stores a real exchange into a real <see cref="InMemoryVectorStore"/> through
/// <see cref="PersistentConversationMemory"/>, and recalls it back out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Milestone 6, Phase 6.2 (§2(b) of the phase design).</b> <c>Rag.NET.Memory</c> had no test
/// project at all before this — its only coverage came from <c>Rag.NET.Tests</c>, which references
/// it transitively and asserts registration. Nothing had ever put a conversation turn in and taken
/// it back out.
/// </para>
/// <para>
/// <b>What is real here and what is not, stated rather than implied.</b> The vector store is
/// <see cref="InMemoryVectorStore"/> — a shipped implementation with real cosine scoring and real
/// top-k selection, not a substitute. The embedder is a stub, and it has to be: a real embedding
/// model is Phase 6.1's territory, and this package's own behaviour does not depend on embedding
/// quality — it depends on whether it stores what it claims to store and finds it again. The stub
/// is deterministic and distinguishes the texts under test, which is exactly the property the
/// recall path needs to be meaningful.
/// </para>
/// <para>
/// This package does not persist to disk itself; it delegates to whichever
/// <see cref="IVectorStore"/> is registered. So "survives a restart" is asserted the way it can
/// honestly be asserted for this package — a <b>new</b> <see cref="PersistentConversationMemory"/>
/// over the <b>same</b> store recalls what its predecessor wrote. That is the real seam:
/// per-instance state (the session chunk counters) must not be what recall depends on.
/// </para>
/// </remarks>
public sealed class RealStoreRoundTripTests
{
    private static readonly SessionId Session = new("session-1");

    [Fact]
    public async Task AnExchangeStored_IsRecalledFromTheStore()
    {
        var store = new InMemoryVectorStore();
        var memory = BuildMemory(store);

        await memory.StoreAsync(
            "What is the capital of France?",
            "Paris.",
            Session,
            TestContext.Current.CancellationToken);

        var recalled = await memory.ProcessAsync(
            [new ChatMessage(ChatRole.User, "capital of France")],
            TestContext.Current.CancellationToken);

        var text = string.Join("\n", recalled.Select(m => m.Text));
        Assert.Contains("Paris.", text, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The chunk counters live in a per-instance <c>ConcurrentDictionary</c>, and the source
    /// comments note the counter resets on restart. This asserts recall does not depend on it: a
    /// fresh instance over the same store must still find what the previous one wrote.
    /// </remarks>
    [Fact]
    public async Task AFreshMemoryInstance_RecallsWhatTheEarlierOneWrote()
    {
        var store = new InMemoryVectorStore();

        var first = BuildMemory(store);
        await first.StoreAsync(
            "What is the capital of France?",
            "Paris.",
            Session,
            TestContext.Current.CancellationToken);

        // Simulate a restart: same store, new instance, counters back to zero.
        var second = BuildMemory(store);
        var recalled = await second.ProcessAsync(
            [new ChatMessage(ChatRole.User, "capital of France")],
            TestContext.Current.CancellationToken);

        var text = string.Join("\n", recalled.Select(m => m.Text));
        Assert.Contains("Paris.", text, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Two exchanges in one session must both survive. The counter increments per store call, so a
    /// collision would silently overwrite the first — the failure the source's own comment warns
    /// about ("index collisions are possible").
    /// </remarks>
    [Fact]
    public async Task TwoExchangesInOneSession_BothSurvive()
    {
        var store = new InMemoryVectorStore();
        var memory = BuildMemory(store);

        await memory.StoreAsync("Capital of France?", "Paris.", Session, TestContext.Current.CancellationToken);
        await memory.StoreAsync("Capital of Japan?", "Tokyo.", Session, TestContext.Current.CancellationToken);

        // Ask the store directly for everything: the memory's own recall is top-k limited.
        var all = await store.SearchAsync(
            new StubEmbedder().Embed("Capital"),
            new SearchOptions { TopK = 10 },
            TestContext.Current.CancellationToken);

        var text = string.Join("\n", all.Select(r => r.Chunk.Text));
        Assert.Contains("Paris.", text, StringComparison.Ordinal);
        Assert.Contains("Tokyo.", text, StringComparison.Ordinal);
    }

    private static PersistentConversationMemory BuildMemory(IVectorStore store) =>
        new(
            inner: new PassThroughMemory(),
            vectorStore: store,
            embedder: new StubEmbedder(),
            options: new PersistentMemoryOptions { TopK = 5, MinScore = 0d });

    /// <summary>The inner memory this decorator wraps; returns history untouched.</summary>
    private sealed class PassThroughMemory : IConversationMemory
    {
        public Task<IReadOnlyList<ChatMessage>> ProcessAsync(
            IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken = default) =>
            Task.FromResult(history);

        // The decorator under test does the storing; the inner memory is not expected to.
        public Task StoreAsync(
            string userMessage, string assistantMessage, SessionId sessionId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// A deterministic embedder: a bag-of-characters vector, so texts sharing words score higher
    /// against each other than against unrelated ones. Not a model — enough structure for the
    /// recall path to be a real test rather than a coin flip.
    /// </summary>
    private sealed class StubEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        private const int Dimensions = 64;

        public ReadOnlyMemory<float> Embed(string text)
        {
            var vector = new float[Dimensions];
            foreach (var c in text.ToLowerInvariant())
            {
                vector[c % Dimensions] += 1f;
            }

            var norm = MathF.Sqrt(vector.Sum(v => v * v));
            if (norm > 0)
            {
                foreach (ref var component in vector.AsSpan()) { component /= norm; }
            }

            return vector;
        }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                values.Select(v => new Embedding<float>(Embed(v))).ToList()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
