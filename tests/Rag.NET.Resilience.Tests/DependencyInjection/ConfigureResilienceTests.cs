using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Registry;
using Polly.Retry;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Resilience;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

/// <summary>
/// Covers <see cref="ResilienceBuilderExtensions.ConfigureResilience"/> actually applying the <c>"rag-net"</c>
/// pipeline to the registered embedding generator and vector store.
/// </summary>
/// <remarks>
/// Hand-written counter fakes throughout (never a delay): retry counts are asserted from an
/// attempt counter, so the tests are deterministic and carry no wall-clock dependency.
/// Retry tests use a zero-delay custom pipeline for the same reason — the default policy's
/// 1 s exponential back-off would make a retried assertion a sleep.
/// </remarks>
public class ConfigureResilienceTests
{
    // ── Fakes ────────────────────────────────────────────────────────────────

    /// <summary>Fails the first <c>failures</c> attempts with <paramref name="failure"/>, then succeeds.</summary>
    private sealed class CountingEmbeddingGenerator(int failures, Func<Exception> failure)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        public int Attempts { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Attempts <= failures)
            {
                throw failure();
            }

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                [new Embedding<float>(new float[] { 1f })]));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }

    /// <summary>
    /// Records the values every attempt saw and fails the first <c>failures</c> attempts, so a
    /// retry can be asserted to have re-sent identical input rather than an emptied sequence.
    /// </summary>
    private sealed class RecordingEmbeddingGenerator(int failures, Func<Exception> failure)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly List<string[]> _seen = [];

        public IReadOnlyList<string[]> Seen => _seen;

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            // A real generator enumerates its input on every attempt — that is precisely why
            // the decorator must hand it something re-enumerable.
            var snapshot = values.ToArray();
            _seen.Add(snapshot);
            if (_seen.Count <= failures)
            {
                throw failure();
            }

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                [.. snapshot.Select(static _ => new Embedding<float>(new float[] { 1f }))]));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }

    /// <summary>
    /// Signals the caller's token mid-flight and then surfaces the resulting
    /// <see cref="OperationCanceledException"/> — the realistic shape of a cancelled remote call.
    /// </summary>
    private sealed class CancellingEmbeddingGenerator(CancellationTokenSource source)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        public int Attempts { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable: the token was signalled above");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }

    /// <summary>
    /// Counts every wrapped operation separately and fails each one's first
    /// <c>failures</c> attempts, so a retry assertion is possible per operation rather
    /// than for <see cref="SearchAsync"/> alone.
    /// </summary>
    private class CountingVectorStore(int failures, Func<Exception> failure) : IVectorStore
    {
        /// <summary><see cref="SearchAsync"/> attempts.</summary>
        public int Attempts { get; private set; }

        public int StoreAttempts { get; private set; }

        public int DeleteAttempts { get; private set; }

        public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
        {
            StoreAttempts++;
            return StoreAttempts <= failures ? throw failure() : Task.CompletedTask;
        }

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding,
            SearchOptions options,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Attempts <= failures)
            {
                throw failure();
            }

            return Task.FromResult<IReadOnlyList<SearchResult>>([]);
        }

        public Task DeleteByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default)
        {
            DeleteAttempts++;
            return DeleteAttempts <= failures ? throw failure() : Task.CompletedTask;
        }
    }

    private sealed class SparseCountingVectorStore : CountingVectorStore, ISparseSearchable
    {
        // Explicit constructor rather than a primary one: passing the same parameters to the
        // base and also capturing them into this type's state is CS9107.
        private readonly int _failures;
        private readonly Func<Exception> _failure;

        public SparseCountingVectorStore(int failures, Func<Exception> failure)
            : base(failures, failure)
        {
            _failures = failures;
            _failure = failure;
        }

        public int StoreSparseAttempts { get; private set; }

        public int SearchSparseAttempts { get; private set; }

        public Task StoreSparseAsync(
            IReadOnlyList<(EmbeddedChunk Chunk, SparseVector Sparse)> items,
            CancellationToken cancellationToken = default)
        {
            StoreSparseAttempts++;
            return StoreSparseAttempts <= _failures ? throw _failure() : Task.CompletedTask;
        }

        public Task<IReadOnlyList<SearchResult>> SearchSparseAsync(
            SparseVector query,
            SearchOptions options,
            CancellationToken cancellationToken = default)
        {
            SearchSparseAttempts++;
            return SearchSparseAttempts <= _failures
                ? throw _failure()
                : Task.FromResult<IReadOnlyList<SearchResult>>([]);
        }
    }

    /// <summary>Declares RRF-style opaque ranking scores, as <c>FederatedVectorStore</c> does.</summary>
    private sealed class OpaqueCountingVectorStore()
        : CountingVectorStore(0, static () => new InvalidOperationException()), IScoreScaleAware
    {
        public ScoreScale ScoreScale => ScoreScale.OpaqueRanking;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The scale a consumer probe reads off <paramref name="store"/>, exactly as
    /// <c>PersistentConversationMemory</c> does it: absence of the interface means
    /// <see cref="ScoreScale.Similarity"/>.
    /// </summary>
    private static ScoreScale EffectiveScale(IVectorStore store) =>
        store is IScoreScaleAware aware ? aware.ScoreScale : ScoreScale.Similarity;

    /// <summary>Three retries with no delay: deterministic and instant (no sleeps).</summary>
    private static void ImmediateRetry(ResiliencePipelineBuilder builder) =>
        builder.AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.Zero,
            BackoffType = DelayBackoffType.Constant,
            UseJitter = false,
        });

    private static Func<Exception> Transient => static () => new InvalidOperationException("transient");

    private static Func<Exception> Cancelled => static () => new OperationCanceledException("cancelled");

    /// <summary>
    /// A genuinely one-shot sequence: it drains <paramref name="source"/>, so a second
    /// enumeration yields nothing at all — the silent shape of the failure, not an exception.
    /// </summary>
    /// <remarks>
    /// A <c>yield return</c> method is only one-shot when its body consumes external state, as
    /// this one does; an iterator over an immutable collection restarts on every
    /// <c>GetEnumerator</c> and would not exercise the guarantee. The returned sequence is not
    /// an <see cref="IReadOnlyList{T}"/>, so the decorator's <c>as</c> fast path does not apply
    /// and the defensive copy is the only thing that can save a retry.
    /// </remarks>
    private static IEnumerable<string> DrainOnce(Queue<string> source)
    {
        while (source.TryDequeue(out var value))
        {
            yield return value;
        }
    }

    private static EmbeddedChunk Chunk() =>
        new()
        {
            Chunk = new TextChunk { Text = "t", DocumentId = new DocumentId("doc1"), ChunkIndex = 0 },
            Embedding = new float[] { 1f },
        };

    /// <summary>Builds the graph with <paramref name="store"/> decorated by the zero-delay pipeline.</summary>
    private static IVectorStore ResilientStoreOver(IVectorStore store) =>
        new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.Services.AddSingleton(store);
                rag.ConfigureResilience(ImmediateRetry);
            })
            .BuildServiceProvider()
            .GetRequiredService<IVectorStore>();

    // ── 1. Embedding retry ───────────────────────────────────────────────────

    [Fact]
    public async Task EmbeddingGenerator_TransientFailure_IsRetried()
    {
        var inner = new CountingEmbeddingGenerator(2, Transient);
        var provider = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(inner);
                rag.ConfigureResilience(ImmediateRetry);
            })
            .BuildServiceProvider();

        var generator = provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        Assert.IsType<ResilientEmbeddingGenerator>(generator);

        var result = await generator.GenerateAsync(["x"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal(3, inner.Attempts); // 2 failures + 1 success
    }

    /// <summary>
    /// The materialise-once guarantee: the caller's sequence is enumerated exactly once, before
    /// the pipeline runs, so every retried attempt re-sends the same values. Without the copy
    /// the second attempt would re-enumerate a spent sequence — silent data loss on a code path
    /// that only runs when something has already gone wrong.
    /// </summary>
    [Fact]
    public async Task EmbeddingGenerator_OneShotSequence_IsMaterialisedOnceAndReplayedOnEveryRetry()
    {
        var inner = new RecordingEmbeddingGenerator(2, Transient);
        var provider = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(inner);
                rag.ConfigureResilience(ImmediateRetry);
            })
            .BuildServiceProvider();

        var source = new Queue<string>(["alpha", "beta"]);
        var result = await provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()
            .GenerateAsync(DrainOnce(source), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Empty(source);              // enumerated exactly once, by the decorator
        Assert.Equal(3, inner.Seen.Count); // 2 failures + 1 success
        Assert.All(inner.Seen, seen => Assert.Equal(new[] { "alpha", "beta" }, seen));
    }

    // ── 2. Vector-store retry ────────────────────────────────────────────────

    [Fact]
    public async Task VectorStore_TransientFailure_IsRetried()
    {
        var inner = new CountingVectorStore(2, Transient);
        var provider = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.Services.AddSingleton<IVectorStore>(inner);
                rag.ConfigureResilience(ImmediateRetry);
            })
            .BuildServiceProvider();

        var store = provider.GetRequiredService<IVectorStore>();
        Assert.IsType<ResilientVectorStore>(store);

        var results = await store.SearchAsync(
            new float[] { 1f }, new SearchOptions(), TestContext.Current.CancellationToken);

        Assert.Empty(results);
        Assert.Equal(3, inner.Attempts);
    }

    /// <summary>
    /// Writes are retried too. This is the headline risk of the whole decorator — a re-sent
    /// write — and the operation that must not quietly degrade into a direct <c>Inner</c> call.
    /// </summary>
    [Fact]
    public async Task VectorStore_StoreAsync_TransientFailure_IsRetried()
    {
        var inner = new CountingVectorStore(2, Transient);

        await ResilientStoreOver(inner).StoreAsync([Chunk()], TestContext.Current.CancellationToken);

        Assert.Equal(3, inner.StoreAttempts); // 2 failures + 1 success
        Assert.Equal(0, inner.Attempts);      // nothing else was touched
    }

    [Fact]
    public async Task VectorStore_DeleteByDocumentIdAsync_TransientFailure_IsRetried()
    {
        var inner = new CountingVectorStore(2, Transient);

        await ResilientStoreOver(inner).DeleteByDocumentIdAsync("doc1", TestContext.Current.CancellationToken);

        Assert.Equal(3, inner.DeleteAttempts);
        Assert.Equal(0, inner.StoreAttempts);
    }

    // ── 2b. Sparse-variant retry ─────────────────────────────────────────────

    [Fact]
    public async Task SparseVectorStore_StoreSparseAsync_TransientFailure_IsRetried()
    {
        var inner = new SparseCountingVectorStore(2, Transient);
        var sparse = Assert.IsAssignableFrom<ISparseSearchable>(ResilientStoreOver(inner));

        await sparse.StoreSparseAsync(
            [(Chunk(), SparseVector.Empty)], TestContext.Current.CancellationToken);

        Assert.Equal(3, inner.StoreSparseAttempts);
        Assert.Equal(0, inner.SearchSparseAttempts);
    }

    [Fact]
    public async Task SparseVectorStore_SearchSparseAsync_TransientFailure_IsRetried()
    {
        var inner = new SparseCountingVectorStore(2, Transient);
        var sparse = Assert.IsAssignableFrom<ISparseSearchable>(ResilientStoreOver(inner));

        var results = await sparse.SearchSparseAsync(
            SparseVector.Empty, new SearchOptions(), TestContext.Current.CancellationToken);

        Assert.Empty(results);
        Assert.Equal(3, inner.SearchSparseAttempts);
        Assert.Equal(0, inner.StoreSparseAttempts);
    }

    // ── 3. Cancellation is never retried ─────────────────────────────────────

    /// <summary>
    /// The caller's token is signalled while the first attempt is in flight, so the call
    /// surfaces an <see cref="OperationCanceledException"/>. Exactly one attempt is made:
    /// cancellation is never a retryable failure. (A token that is <em>already</em> cancelled
    /// short-circuits even earlier — Polly never invokes the callback at all.)
    /// </summary>
    [Fact]
    public async Task Cancellation_IsNotRetried()
    {
        using var cts = new CancellationTokenSource();
        var inner = new CancellingEmbeddingGenerator(cts);
        var provider = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(inner);
                rag.ConfigureResilience(); // the default policy owns the cancellation predicate
            })
            .BuildServiceProvider();

        var generator = provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => generator.GenerateAsync(["x"], cancellationToken: cts.Token));

        Assert.Equal(1, inner.Attempts); // exactly one attempt — never retried
    }

    /// <summary>
    /// The strong form of the cancellation guarantee: even with a live (never-signalled) token,
    /// an <see cref="OperationCanceledException"/> is not a transient failure. This isolates the
    /// default policy's <c>ShouldHandle</c> predicate — Polly's own "stop when the context token
    /// is cancelled" check cannot be what stops the retry here, because the token is not cancelled.
    /// </summary>
    [Fact]
    public async Task OperationCanceledException_WithLiveToken_IsExcludedByTheRetryPredicate()
    {
        var inner = new CountingEmbeddingGenerator(int.MaxValue, Cancelled);
        var provider = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(inner);
                rag.ConfigureResilience();
            })
            .BuildServiceProvider();

        var generator = provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => generator.GenerateAsync(["x"], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, inner.Attempts);
    }

    /// <summary>
    /// A blown budget is a kill switch, not a provider blip: the default predicate excludes it,
    /// so it is not retried (which would otherwise burn the whole back-off budget on a call that
    /// cannot succeed).
    /// </summary>
    [Fact]
    public async Task BudgetExceededException_IsNotRetried()
    {
        var inner = new CountingEmbeddingGenerator(
            int.MaxValue, static () => new BudgetExceededException(CostWindow.Day, 1m, 2m));
        var provider = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(inner);
                rag.ConfigureResilience();
            })
            .BuildServiceProvider();

        var generator = provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        await Assert.ThrowsAsync<BudgetExceededException>(
            () => generator.GenerateAsync(["x"], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, inner.Attempts);
    }

    /// <summary>The pipeline is no longer dangling: it is resolvable under the documented name.</summary>
    [Fact]
    public void ConfigureResilience_RegistersThePipelineUnderTheDocumentedName()
    {
        var provider = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.Services.AddSingleton<IVectorStore>(new CountingVectorStore(0, Transient));
                rag.ConfigureResilience();
            })
            .BuildServiceProvider();

        // The pipeline itself is resolvable under the documented name.
        var pipeline = provider.GetRequiredService<ResiliencePipelineProvider<string>>()
            .GetPipeline(ResilienceBuilderExtensions.ResiliencePipelineName);

        Assert.NotNull(pipeline);
    }

    // ── 4. Untouched graph without the call ──────────────────────────────────

    [Fact]
    public void WithoutConfigureResilience_NoDecoratorRegistered()
    {
        var embedding = new CountingEmbeddingGenerator(0, Transient);
        var store = new CountingVectorStore(0, Transient);
        var provider = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(embedding);
                rag.Services.AddSingleton<IVectorStore>(store);
            })
            .BuildServiceProvider();

        Assert.Same(embedding, provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
        Assert.Same(store, provider.GetRequiredService<IVectorStore>());
        Assert.Null(provider.GetService<ResiliencePipelineProvider<string>>());
    }

    // ── 5. Lifetime preservation (ServiceDecorationHelper contract) ───────────

    [Fact]
    public void Decoration_PreservesTheRegisteredLifetime()
    {
        var services = new ServiceCollection();
        services.AddRagNet(rag =>
        {
            rag.Services.AddScoped<IVectorStore>(_ => new CountingVectorStore(0, Transient));
            rag.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                _ => new CountingEmbeddingGenerator(0, Transient));
            rag.ConfigureResilience(ImmediateRetry);
        });

        var storeDescriptor = services.Last(d => !d.IsKeyedService && d.ServiceType == typeof(IVectorStore));
        var embeddingDescriptor = services.Last(d =>
            !d.IsKeyedService && d.ServiceType == typeof(IEmbeddingGenerator<string, Embedding<float>>));
        Assert.Equal(ServiceLifetime.Scoped, storeDescriptor.Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, embeddingDescriptor.Lifetime);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();

        var fromScope1A = scope1.ServiceProvider.GetRequiredService<IVectorStore>();
        var fromScope1B = scope1.ServiceProvider.GetRequiredService<IVectorStore>();
        var fromScope2 = scope2.ServiceProvider.GetRequiredService<IVectorStore>();

        Assert.IsType<ResilientVectorStore>(fromScope1A);
        Assert.Same(fromScope1A, fromScope1B);   // cached within a scope
        Assert.NotSame(fromScope1A, fromScope2); // a distinct decorator per scope

        Assert.Same(
            provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
            provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
    }

    // ── Capability preservation, ordering, idempotence ───────────────────────

    [Fact]
    public void Decoration_PreservesTheSparseCapabilityProbe()
    {
        var provider = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.Services.AddSingleton<IVectorStore>(new SparseCountingVectorStore(0, Transient));
                rag.ConfigureResilience(ImmediateRetry);
            })
            .BuildServiceProvider();

        Assert.IsType<ResilientSparseVectorStore>(provider.GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void Decoration_OfADenseOnlyStore_DoesNotFakeTheSparseCapability()
    {
        var provider = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.Services.AddSingleton<IVectorStore>(new CountingVectorStore(0, Transient));
                rag.ConfigureResilience(ImmediateRetry);
            })
            .BuildServiceProvider();

        Assert.False(provider.GetRequiredService<IVectorStore>() is ISparseSearchable);
    }

    /// <summary>
    /// The reported bug's exact shape: <c>UseFederatedSearch</c> + <c>ConfigureResilience</c>.
    /// The decorator must not mask the inner store's <see cref="ScoreScale.OpaqueRanking"/>
    /// declaration — a consumer that reads Similarity off a federated store applies a
    /// similarity-calibrated threshold to RRF scores that peak near 0.033 and recalls nothing.
    /// </summary>
    [Fact]
    public void Decoration_OfAFederatedStore_PreservesTheOpaqueScoreScaleProbe()
    {
        var provider = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.UseFederatedSearch(f => f
                    .AddStore(_ => new CountingVectorStore(0, Transient))
                    .AddStore(_ => new CountingVectorStore(0, Transient)));
                rag.ConfigureResilience(ImmediateRetry);
            })
            .BuildServiceProvider();

        var store = provider.GetRequiredService<IVectorStore>();

        Assert.IsType<ResilientVectorStore>(store);
        Assert.Equal(ScoreScale.OpaqueRanking, EffectiveScale(store));
    }

    /// <summary>The same guarantee for any opaque store, federated or not.</summary>
    [Fact]
    public void Decoration_PreservesTheOpaqueScoreScaleProbe()
    {
        var provider = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.Services.AddSingleton<IVectorStore>(new OpaqueCountingVectorStore());
                rag.ConfigureResilience(ImmediateRetry);
            })
            .BuildServiceProvider();

        Assert.Equal(ScoreScale.OpaqueRanking, EffectiveScale(provider.GetRequiredService<IVectorStore>()));
    }

    /// <summary>
    /// The negative direction: decorating a store that declares no scale must not falsely
    /// upgrade it. Whether the decorator implements the interface or not, the probe reads
    /// <see cref="ScoreScale.Similarity"/> — the documented meaning of its absence.
    /// </summary>
    [Fact]
    public void Decoration_OfASimilarityStore_DoesNotFakeAnOpaqueScale()
    {
        var inner = new CountingVectorStore(0, Transient);
        Assert.IsNotAssignableFrom<IScoreScaleAware>(inner);

        var provider = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.Services.AddSingleton<IVectorStore>(inner);
                rag.ConfigureResilience(ImmediateRetry);
            })
            .BuildServiceProvider();

        Assert.Equal(ScoreScale.Similarity, EffectiveScale(provider.GetRequiredService<IVectorStore>()));
    }

    /// <summary>The sparse decorator variant inherits the same forwarding.</summary>
    [Fact]
    public void SparseDecoration_PreservesTheScoreScaleProbe()
    {
        var store = ResilientVectorStore.Create(new SparseCountingVectorStore(0, Transient), ResiliencePipeline.Empty);

        Assert.IsType<ResilientSparseVectorStore>(store);
        Assert.Equal(ScoreScale.Similarity, EffectiveScale(store));
    }

    /// <summary>
    /// Decoration must not manufacture a chunk-lookup capability the inner store does not have.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ResilientVectorStore"/> implements <see cref="IChunkLookup"/> unconditionally, so
    /// that it can forward one — the alternative being a decorator variant per capability
    /// combination. That makes an <c>is IChunkLookup</c> test answer <see langword="true"/> for
    /// every decorated store, which is why the interface carries
    /// <see cref="IChunkLookup.SupportsChunkLookup"/> and why this asserts on that rather than on
    /// the type.
    /// </para>
    /// <para>
    /// Getting this wrong is quiet: GraphRAG's local search would read the empty result as a graph
    /// whose entities have no source chunks, spend none of the half of its token budget reserved
    /// for them, and report nothing unusual.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Decoration_DoesNotClaimAChunkLookupTheInnerStoreLacks()
    {
        var ct = TestContext.Current.CancellationToken;
        var withoutLookup = ResilientVectorStore.Create(
            new CountingVectorStore(0, Transient), ResiliencePipeline.Empty);

        Assert.IsAssignableFrom<IChunkLookup>(withoutLookup);
        Assert.False(((IChunkLookup)withoutLookup).SupportsChunkLookup);
        Assert.Empty(await ((IChunkLookup)withoutLookup).GetChunksAsync([new ChunkKey("d", 0)], ct));
    }

    /// <summary>And a capability the inner store does have survives decoration.</summary>
    [Fact]
    public async Task Decoration_PreservesAChunkLookupTheInnerStoreHas()
    {
        var ct = TestContext.Current.CancellationToken;
        using var inner = new InMemoryVectorStore();
        await inner.StoreAsync(
        [
            new EmbeddedChunk
            {
                Chunk = new TextChunk
                {
                    Text = "kept",
                    DocumentId = new DocumentId("doc"),
                    ChunkIndex = 0,
                },
                Embedding = new float[] { 1f, 0f },
            },
        ], ct);

        var decorated = (IChunkLookup)ResilientVectorStore.Create(inner, ResiliencePipeline.Empty);

        Assert.True(decorated.SupportsChunkLookup);
        var found = await decorated.GetChunksAsync([new ChunkKey("doc", 0)], ct);
        Assert.Equal("kept", Assert.Single(found).Text, StringComparer.Ordinal);
    }

    [Fact]
    public void ConfigureResilience_WithNothingToDecorate_ThrowsActionable()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddRagNet(rag => rag.ConfigureResilience()));

        Assert.Contains("ConfigureResilience", ex.Message, StringComparison.Ordinal);
        Assert.Contains("before", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #195: the "nothing to apply to" guard fires only when <b>neither</b> surface is
    /// registered, so registering one of the two and the other afterwards left the second silently
    /// undecorated — retried store, unretried embedding calls, and no exception because one surface
    /// <i>was</i> found. A registration that happens later cannot be seen at registration time, so
    /// this is caught when the pipeline is resolved instead.
    /// </summary>
    [Fact]
    public void ConfigureResilience_BeforeTheEmbeddingGenerator_FailsLoudlyRatherThanSkippingIt()
    {
        var services = new ServiceCollection();
        services.AddRagNet(rag =>
        {
            rag.Services.AddSingleton<IVectorStore>(new CountingVectorStore(0, Transient));
            rag.ConfigureResilience(ImmediateRetry);
        });

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new CountingEmbeddingGenerator(0, Transient));

        var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IRagPipeline>());

        Assert.Contains("ConfigureResilience", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IEmbeddingGenerator", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// One layer means 4 attempts (1 + 3 retries); two stacked layers would multiply to 16.
    /// A store that fails exactly 4 times therefore still throws when there is one layer and
    /// would silently succeed if the decorators had stacked.
    /// </summary>
    [Fact]
    public async Task ConfigureResilience_CalledTwice_DoesNotStackDecorators()
    {
        var inner = new CountingVectorStore(4, Transient);
        var provider = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.Services.AddSingleton<IVectorStore>(inner);
                rag.ConfigureResilience(ImmediateRetry);
                rag.ConfigureResilience(ImmediateRetry);
            })
            .BuildServiceProvider();

        var store = Assert.IsType<ResilientVectorStore>(provider.GetRequiredService<IVectorStore>());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SearchAsync(new float[] { 1f }, new SearchOptions(), TestContext.Current.CancellationToken));

        Assert.Equal(4, inner.Attempts);
    }
}
