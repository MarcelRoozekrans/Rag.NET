using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rag.NET.Abstractions;
using Rag.NET.Evaluation.Shadow;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Shadow;

/// <summary>
/// <c>UseShadow</c>: the registration seam. The properties pinned here are the ones a user
/// discovers the hard way — registering does nothing until a rate is set, a bad rate fails at
/// registration rather than silently at runtime, and the consumer the host runs is the same
/// instance whose counters an operator reads.
/// </summary>
[Collection("ShadowTelemetry")] // emits shadow counters; see ShadowTelemetryCollection
public sealed class RagBuilderExtensionsTests
{
    [Fact]
    public void UseShadow_WithoutARegisteredPipeline_ThrowsAtRegistration()
    {
        var builder = NewBuilder(new ServiceCollection());

        Assert.Throws<InvalidOperationException>(() => builder.UseShadow<ScriptedRagPipeline>());
    }

    [Fact]
    public void UseShadow_AnOutOfRangeRate_ThrowsAtRegistration()
    {
        // Rejected the moment the configure delegate runs — not clamped, and not deferred to
        // the first request, where the stack trace would point at the wrong code.
        var services = new ServiceCollection();
        services.AddSingleton<IRagPipeline>(new ScriptedRagPipeline());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => NewBuilder(services).UseShadow<ScriptedRagPipeline>(o => o.SampleRate = 1.5));
    }

    [Fact]
    public async Task UseShadow_DecoratesThePipeline_AndAskStillReturnsThePrimaryAnswer()
    {
        var secondary = new ScriptedRagPipeline();
        await using var provider = BuildShadowed(secondary, o => o.SampleRate = 1.0);

        var pipeline = provider.GetRequiredService<IRagPipeline>();
        var shadowed = Assert.IsType<ShadowRagPipeline>(pipeline);
        var response = await shadowed.AskAsync("q", options: null, TestContext.Current.CancellationToken);

        Assert.Equal("primary-answer", response.Answer);
        var queue = provider.GetRequiredService<ShadowCaptureQueue>();
        Assert.True(queue.TryDequeue(out var pending));
        Assert.Equal("q", pending.Question);
        // The shadowed pipeline is the container's TSecondary instance, not a second copy.
        Assert.Same(secondary, pending.SecondaryPipeline);
    }

    /// <summary>
    /// The headline default: registering shadow mode without setting a rate shadows nothing —
    /// nothing queued, and the secondary never invoked. Nobody discovers doubled spend by
    /// upgrading a package.
    /// </summary>
    [Fact]
    public async Task UseShadow_WithoutARate_ShadowsNothing()
    {
        var secondary = new ScriptedRagPipeline();
        await using var provider = BuildShadowed(secondary, configure: null);

        var pipeline = provider.GetRequiredService<IRagPipeline>();
        var response = await pipeline.AskAsync("q", options: null, TestContext.Current.CancellationToken);

        Assert.Equal("primary-answer", response.Answer);
        Assert.Equal(0, provider.GetRequiredService<ShadowCaptureQueue>().PendingCount);
        Assert.Equal(0, secondary.AskCalls);
    }

    /// <summary>
    /// The hosted consumer and the resolvable consumer are the same instance: counters like
    /// <see cref="ShadowCaptureConsumer.AbandonedCount"/> would be meaningless read off a
    /// different object than the one the host actually ran.
    /// </summary>
    [Fact]
    public async Task UseShadow_RegistersTheConsumerAsAHostedService_AsTheSameResolvableInstance()
    {
        await using var provider = BuildShadowed(new ScriptedRagPipeline(), configure: null);

        var hosted = Assert.Single(provider.GetServices<IHostedService>());
        Assert.Same(provider.GetRequiredService<ShadowCaptureConsumer>(), hosted);
    }

    [Fact]
    public async Task UseShadow_DefaultsToTheInMemoryStore_ButRespectsAnApplicationRegisteredOne()
    {
        await using var defaulted = BuildShadowed(new ScriptedRagPipeline(), configure: null);
        Assert.IsType<InMemoryShadowCaptureStore>(defaulted.GetRequiredService<IShadowCaptureStore>());

        var services = new ServiceCollection();
        services.AddSingleton<IRagPipeline>(Primary());
        var applicationStore = new RecordingStore();
        services.AddSingleton<IShadowCaptureStore>(applicationStore);
        NewBuilder(services).UseShadow<ScriptedRagPipeline>();
        await using var overridden = services.BuildServiceProvider();

        Assert.Same(applicationStore, overridden.GetRequiredService<IShadowCaptureStore>());
    }

    /// <summary>
    /// End to end through the container at rate 1: the hosted consumer runs the secondary
    /// out-of-band and the pair lands in the store. Rate 1 never consults the random source, so
    /// this is deterministic against the default sampler.
    /// </summary>
    [Fact]
    public async Task UseShadow_EndToEnd_TheConsumerPersistsThePair()
    {
        var secondary = new ScriptedRagPipeline((_, _, _) =>
            Task.FromResult(ScriptedRagPipeline.Response("shadow-answer", "shadow-context")));
        await using var provider = BuildShadowed(secondary, o => o.SampleRate = 1.0);
        var consumer = provider.GetRequiredService<ShadowCaptureConsumer>();
        await consumer.StartAsync(TestContext.Current.CancellationToken);

        var pipeline = provider.GetRequiredService<IRagPipeline>();
        await pipeline.AskAsync("q", options: null, TestContext.Current.CancellationToken);
        await consumer.StopAsync(CancellationToken.None).WaitAsync(TestContext.Current.CancellationToken);

        var store = Assert.IsType<InMemoryShadowCaptureStore>(provider.GetRequiredService<IShadowCaptureStore>());
        var capture = Assert.Single(store.Snapshot());
        Assert.Equal("q", capture.Question);
        Assert.Equal("primary-answer", capture.Primary.Answer);
        Assert.Equal("shadow-answer", capture.Secondary.Answer);
    }

    [Fact]
    public void UseShadow_NullBuilder_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => RagBuilderExtensions.UseShadow<ScriptedRagPipeline>(null!));
    }

    private static ScriptedRagPipeline Primary() => new((_, _, _) =>
        Task.FromResult(ScriptedRagPipeline.Response("primary-answer", "primary-context")));

    /// <summary>A provider with a primary, the given secondary instance, and shadow mode registered.</summary>
    private static ServiceProvider BuildShadowed(
        ScriptedRagPipeline secondary,
        Action<ShadowPipelineOptions>? configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRagPipeline>(Primary());
        services.AddSingleton(secondary);
        NewBuilder(services).UseShadow<ScriptedRagPipeline>(configure);
        return services.BuildServiceProvider();
    }

    private static IRagBuilder NewBuilder(IServiceCollection services) => new TestRagBuilder(services);

    /// <summary>
    /// The minimal <see cref="IRagBuilder"/>: <c>UseShadow</c> must need nothing beyond
    /// <see cref="IRagBuilder.Services"/>, so anything else throwing proves it stayed minimal.
    /// </summary>
    private sealed class TestRagBuilder(IServiceCollection services) : IRagBuilder
    {
        public IServiceCollection Services { get; } = services;

        public IRagBuilder AddParser<TParser>(Type? replaces = null, string[]? replacesTypeNames = null)
            where TParser : class, IDocumentParser =>
            throw new NotSupportedException("UseShadow must only touch Services.");

        public IRagBuilder UseReranking<TReranker>() where TReranker : class, IReranker =>
            throw new NotSupportedException("UseShadow must only touch Services.");
    }

    private sealed class RecordingStore : IShadowCaptureStore
    {
        public Task SaveAsync(ShadowCapture capture, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
