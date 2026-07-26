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
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

/// <summary>
/// Covers <see cref="RagBuilder.ConfigureResilience"/> actually applying the <c>"rag-net"</c>
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

    private class CountingVectorStore(int failures, Func<Exception> failure) : IVectorStore
    {
        public int Attempts { get; private set; }

        public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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

        public Task DeleteByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class SparseCountingVectorStore() : CountingVectorStore(0, static () => new InvalidOperationException()),
        ISparseSearchable
    {
        public Task StoreSparseAsync(
            IReadOnlyList<(EmbeddedChunk Chunk, SparseVector Sparse)> items,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<SearchResult>> SearchSparseAsync(
            SparseVector query,
            SearchOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchResult>>([]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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
            .GetPipeline(RagBuilder.ResiliencePipelineName);

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
                rag.Services.AddSingleton<IVectorStore>(new SparseCountingVectorStore());
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
