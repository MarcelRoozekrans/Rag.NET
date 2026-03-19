using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class PipelineExtensibilityTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    private static ServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        embedder.GenerateAsync(
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<EmbeddingGenerationOptions?>(),
            Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>(
                [new Embedding<float>(new float[] { 0.1f })]));

        vectorStore.SearchAsync(
            Arg.Any<ReadOnlyMemory<float>>(),
            Arg.Any<SearchOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        services.AddSingleton(vectorStore);
        services.AddSingleton(embedder);
        return services;
    }

    // ── Retrieval extensibility ───────────────────────────────────────────

    [Fact]
    public async Task RetrievalPipeline_CustomBehaviorAddedBeforeVectorStore_Executes()
    {
        // A tracking behavior records whether its HandleAsync was invoked
        var executed = false;

        var services = BaseServices();
        services.AddSingleton(new TrackingRetrievalBehavior(() => executed = true));

        services.AddRagNet(
            retrieval: b => b.Add<TrackingRetrievalBehavior>(before: typeof(VectorStoreBehavior)));

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        await pipeline.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(executed, "Custom retrieval behavior should have executed");
    }

    [Fact]
    public async Task RetrievalPipeline_CustomBehaviorCanModifyContext()
    {
        // A behavior that overrides the query text — downstream sees the modified value
        string? seenQuery = null;

        var services = BaseServices();
        services.AddSingleton(new QueryOverrideBehavior("overridden-query"));
        services.AddSingleton(new QueryCaptureBehavior(q => seenQuery = q));

        services.AddRagNet(retrieval: b =>
        {
            b.Add<QueryOverrideBehavior>(before: typeof(VectorStoreBehavior));
            b.Add<QueryCaptureBehavior>(after: typeof(QueryOverrideBehavior));
        });

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        await pipeline.RetrieveAsync("original-query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("overridden-query", seenQuery);
    }

    [Fact]
    public async Task RetrievalPipeline_ReplacedBehavior_NewBehaviorExecutes_OldDoesNot()
    {
        var oldExecuted = false;
        var newExecuted = false;

        var services = BaseServices();
        services.AddSingleton(new TrackingRetrievalBehavior(() => oldExecuted = true));
        services.AddSingleton(new AlternateTrackingRetrievalBehavior(() => newExecuted = true));

        // Replace LostInTheMiddleBehavior with AlternateTrackingRetrievalBehavior
        services.AddRagNet(retrieval: b =>
            b.Replace<LostInTheMiddleBehavior, AlternateTrackingRetrievalBehavior>());

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        await pipeline.RetrieveAsync("query",
            new RetrievalOptions { UseLostInTheMiddleReordering = true },
            TestContext.Current.CancellationToken);

        Assert.False(oldExecuted, "Replaced (old) behavior should NOT execute");
        Assert.True(newExecuted, "Replacement (new) behavior should execute");
    }

    // ── Ingestion extensibility ───────────────────────────────────────────

    [Fact]
    public async Task IngestionPipeline_CustomBehaviorAddedAfterParseBehavior_Executes()
    {
        var executed = false;

        var services = BaseServices();
        services.AddSingleton(new TrackingIngestionBehavior(() => executed = true));

        services.AddRagNet(
            ingestion: b => b.Add<TrackingIngestionBehavior>(after: typeof(ParseBehavior)));

        var sp = services.BuildServiceProvider();
        var ingestor = sp.GetRequiredService<IIngestor>();

        await ingestor.IngestAsync(
            new MemoryStream("hello world"u8.ToArray()),
            new DocumentMetadata { DocumentId = "doc-1", FileName = "test.txt" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(executed, "Custom ingestion behavior should have executed");
    }

    // ── Helper behavior types ─────────────────────────────────────────────

    private sealed class TrackingRetrievalBehavior(Action onExecute) : IRetrievalBehavior
    {
        public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
            RetrievalContext ctx, CancellationToken ct,
            Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
        {
            onExecute();
            return await next(ctx, ct).ConfigureAwait(false);
        }
    }

    private sealed class AlternateTrackingRetrievalBehavior(Action onExecute) : IRetrievalBehavior
    {
        public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
            RetrievalContext ctx, CancellationToken ct,
            Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
        {
            onExecute();
            return await next(ctx, ct).ConfigureAwait(false);
        }
    }

    private sealed class QueryOverrideBehavior(string overrideQuery) : IRetrievalBehavior
    {
        public ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
            RetrievalContext ctx, CancellationToken ct,
            Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
            => next(ctx with { Query = overrideQuery }, ct);
    }

    private sealed class QueryCaptureBehavior(Action<string> capture) : IRetrievalBehavior
    {
        public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
            RetrievalContext ctx, CancellationToken ct,
            Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
        {
            capture(ctx.Query);
            return await next(ctx, ct).ConfigureAwait(false);
        }
    }

    private sealed class TrackingIngestionBehavior(Action onExecute) : IIngestionBehavior
    {
        public async ValueTask<IngestionResult> HandleAsync(
            IngestionContext ctx, CancellationToken ct,
            Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
        {
            onExecute();
            return await next(ctx, ct).ConfigureAwait(false);
        }
    }
}
