using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Evaluation.Shadow;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Shadow;

/// <summary>
/// The sanitiser seam (design §5): captures hold whatever real users typed plus retrieved
/// document text, verbatim, so an application can put a scrubber between the consumer and the
/// store. The property worth pinning is the failure direction: a capture the sanitiser cannot
/// handle is <b>lost and counted</b>, never persisted unsanitised — the safe side of that
/// trade — and by default, with no sanitiser registered, everything persists verbatim as a
/// deliberate documented choice.
/// </summary>
[Collection("ShadowTelemetry")] // emits shadow counters; see ShadowTelemetryCollection
public sealed class ShadowCaptureSanitiserTests
{
    [Fact]
    public async Task Consumer_NoSanitiser_PersistsTheCaptureVerbatim()
    {
        var store = new CollectingStore();
        var (queue, consumer) = NewConsumer(store, sanitiser: null);
        using var _ = consumer;
        await consumer.StartAsync(TestContext.Current.CancellationToken);

        await queue.EnqueueAsync(Pending("the user's exact words"), TestContext.Current.CancellationToken);
        await consumer.StopAsync(CancellationToken.None)
            .WaitAsync(TestContext.Current.CancellationToken);

        var capture = Assert.Single(store.Saved);
        Assert.Equal("the user's exact words", capture.Question);
        Assert.Equal(["c"], capture.Primary.ContextTexts);
    }

    [Fact]
    public async Task Consumer_ASanitiser_RunsBeforeTheStoreSeesTheCapture()
    {
        var store = new CollectingStore();
        var (queue, consumer) = NewConsumer(store, new RedactingSanitiser());
        using var _ = consumer;
        await consumer.StartAsync(TestContext.Current.CancellationToken);

        await queue.EnqueueAsync(Pending("secret question"), TestContext.Current.CancellationToken);
        await consumer.StopAsync(CancellationToken.None)
            .WaitAsync(TestContext.Current.CancellationToken);

        var capture = Assert.Single(store.Saved);
        Assert.Equal("[redacted]", capture.Question);
        Assert.Equal(["[redacted]"], capture.Primary.ContextTexts);
        Assert.Equal(["[redacted]"], capture.Secondary.ContextTexts);
    }

    /// <summary>
    /// A sanitiser that throws costs exactly that capture, recorded as a persistence failure —
    /// lost rather than persisted unsanitised, which is the only safe direction for a seam whose
    /// whole purpose is keeping raw payloads out of the store — and the consumer moves on.
    /// </summary>
    [Fact]
    public async Task Consumer_ASanitiserThatThrows_LosesOnlyThatCapture_AndNothingReachesTheStoreUnsanitised()
    {
        var store = new CollectingStore();
        var (queue, consumer) = NewConsumer(store, new RedactingSanitiser(throwOn: "poison"));
        using var _ = consumer;
        await consumer.StartAsync(TestContext.Current.CancellationToken);

        await queue.EnqueueAsync(Pending("poison"), TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(Pending("survivor"), TestContext.Current.CancellationToken);
        await consumer.StopAsync(CancellationToken.None)
            .WaitAsync(TestContext.Current.CancellationToken);

        var capture = Assert.Single(store.Saved);
        Assert.Equal("[redacted]", capture.Question);
        var failure = Assert.Single(consumer.FailureSnapshot());
        Assert.Equal("poison", failure.Question);
        Assert.Equal("InvalidOperationException: cannot sanitise this one", failure.Message);
    }

    [Fact]
    public async Task Consumer_ASanitiserReturningNull_IsAFailureNotAPersistedNull()
    {
        var store = new CollectingStore();
        var (queue, consumer) = NewConsumer(store, new NullReturningSanitiser());
        using var _ = consumer;
        await consumer.StartAsync(TestContext.Current.CancellationToken);

        await queue.EnqueueAsync(Pending("q"), TestContext.Current.CancellationToken);
        await consumer.StopAsync(CancellationToken.None)
            .WaitAsync(TestContext.Current.CancellationToken);

        Assert.Empty(store.Saved);
        Assert.Single(consumer.FailureSnapshot());
    }

    /// <summary>A sanitiser registered in the container is picked up by <c>UseShadow</c>'s consumer.</summary>
    [Fact]
    public async Task UseShadow_ResolvesARegisteredSanitiser()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRagPipeline>(new ScriptedRagPipeline((_, _, _) =>
            Task.FromResult(ScriptedRagPipeline.Response("primary-answer", "primary-context"))));
        services.AddSingleton(new ScriptedRagPipeline());
        services.AddSingleton<IShadowCaptureSanitiser>(new RedactingSanitiser());
        new TestRagBuilder(services).UseShadow<ScriptedRagPipeline>(o => o.SampleRate = 1.0);
        await using var provider = services.BuildServiceProvider();
        var consumer = provider.GetRequiredService<ShadowCaptureConsumer>();
        await consumer.StartAsync(TestContext.Current.CancellationToken);

        var pipeline = provider.GetRequiredService<IRagPipeline>();
        await pipeline.AskAsync("secret", options: null, TestContext.Current.CancellationToken);
        await consumer.StopAsync(CancellationToken.None)
            .WaitAsync(TestContext.Current.CancellationToken);

        var store = Assert.IsType<InMemoryShadowCaptureStore>(provider.GetRequiredService<IShadowCaptureStore>());
        var capture = Assert.Single(store.Snapshot());
        Assert.Equal("[redacted]", capture.Question);
    }

    private static (ShadowCaptureQueue Queue, ShadowCaptureConsumer Consumer) NewConsumer(
        IShadowCaptureStore store,
        IShadowCaptureSanitiser? sanitiser)
    {
        var queue = new ShadowCaptureQueue(new ShadowCaptureQueueOptions { Capacity = 8 });
        var consumer = new ShadowCaptureConsumer(
            queue,
            store,
            new ShadowCaptureConsumerOptions { DrainTimeout = TimeSpan.FromMinutes(1) },
            sanitiser);
        return (queue, consumer);
    }

    private static PendingShadowCapture Pending(string question) => new(
        question,
        Options: null,
        new ShadowVariantCapture("primary", Answer: "a", ContextTexts: ["c"], Latency: TimeSpan.FromMilliseconds(1)),
        new ScriptedRagPipeline(),
        "shadow",
        new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Replaces the question and every context text; throws on a scripted question.</summary>
    private sealed class RedactingSanitiser(string? throwOn = null) : IShadowCaptureSanitiser
    {
        public Task<ShadowCapture> SanitiseAsync(
            ShadowCapture capture,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(capture.Question, throwOn, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("cannot sanitise this one");
            }

            return Task.FromResult(capture with
            {
                Question = "[redacted]",
                Primary = capture.Primary with { ContextTexts = Redacted(capture.Primary.ContextTexts) },
                Secondary = capture.Secondary with { ContextTexts = Redacted(capture.Secondary.ContextTexts) },
            });
        }

        private static string[] Redacted(IReadOnlyList<string> texts)
        {
            var redacted = new string[texts.Count];
            Array.Fill(redacted, "[redacted]");
            return redacted;
        }
    }

    private sealed class NullReturningSanitiser : IShadowCaptureSanitiser
    {
        public Task<ShadowCapture> SanitiseAsync(
            ShadowCapture capture,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ShadowCapture>(null!);
    }

    private sealed class CollectingStore : IShadowCaptureStore
    {
        private readonly List<ShadowCapture> _saved = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<ShadowCapture> Saved
        {
            get
            {
                lock (_gate)
                {
                    return [.. _saved];
                }
            }
        }

        public Task SaveAsync(ShadowCapture capture, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _saved.Add(capture);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class TestRagBuilder(IServiceCollection services) : IRagBuilder
    {
        public IServiceCollection Services { get; } = services;

        public IRagBuilder AddParser<TParser>(Type? replaces = null, string[]? replacesTypeNames = null)
            where TParser : class, IDocumentParser =>
            throw new NotSupportedException("UseShadow must only touch Services.");

        public IRagBuilder UseReranking<TReranker>() where TReranker : class, IReranker =>
            throw new NotSupportedException("UseShadow must only touch Services.");
    }
}
