using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

/// <summary>
/// Characterisation tests pinning the exact, ordered behaviour composition of the default and
/// configured pipelines before the package decomposition. These must keep passing unchanged
/// after every extraction — a diff in one of these arrays is a behaviour change, which the
/// decomposition phase forbids.
/// </summary>
/// <remarks>
/// The expected orders are hard-coded literals on purpose. Deriving them from
/// <see cref="RetrievalPipelineBuilder"/> or <see cref="IngestionPipelineBuilder"/> would make
/// the assertion a tautology that passes whatever the product does. Note that
/// <c>Add&lt;T&gt;(after:)</c> with an absent anchor silently appends rather than failing, so
/// only an exact, ordered assertion can catch a reordering.
/// </remarks>
public sealed class DefaultCompositionTests
{
    private static readonly string[] DefaultRetrievalOrder =
    [
        "SelfQueryBehavior",
        "ResultCacheBehavior",
        "LostInTheMiddleBehavior",
        "ContextBudgetBehavior",
        "MmrBehavior",
        "RedundancyFilterBehavior",
        "ParentDocumentRetrievalBehavior",
        "RerankingBehavior",
        "RetrievalGuardBehavior",
        "AdaptiveRetrievalBehavior",
        "CorrectiveRagBehavior",
        "MultiQueryBehavior",
        "HydeBehavior",
        "EmbeddingCacheBehavior",
        "FilterBehavior",
        "EnsembleBehavior",
        "VectorStoreBehavior",
    ];

    private static readonly string[] DefaultIngestionOrder =
    [
        "OverwriteBehavior",
        "ParseBehavior",
        "ChunkingBehavior",
        "LlmMetadataExtractionBehavior",
        "MetadataBehavior",
        "TagIngestionBehavior",
        "ChunkSanitiserBehavior",
        "ParentDocumentIngestionBehavior",
        "EmbeddingBehavior",
        "SparseEmbeddingBehavior",
        "StorageBehavior",
    ];

    [Fact]
    public void DefaultRetrievalPipeline_ComposesTheSameBehavioursInTheSameOrder()
    {
        var services = new ServiceCollection();
        services.AddRagNet(rag => { /* no opt-in calls */ });

        var actual = ResolveRetrievalBehaviourOrder(services);

        Assert.Equal(DefaultRetrievalOrder, actual);
    }

    [Fact]
    public void ConfiguredRetrievalPipeline_ComposesTheSameBehavioursInTheSameOrder()
    {
        var services = new ServiceCollection();
        // ConfigureResilience requires an embedding generator or vector store to decorate.
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new StubEmbeddingGenerator());
        var dbPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".db");

        services.AddRagNet(rag => rag
            .UseCaching()
            .ConfigureResilience()
            .UseSqlitePersistence(dbPath));

        var actual = ResolveRetrievalBehaviourOrder(services);

        // The opt-in clusters register services and decorators; the behaviour chain itself
        // is identical to the default. That equality is exactly what this test pins.
        Assert.Equal(DefaultRetrievalOrder, actual);
    }

    [Fact]
    public void DefaultIngestionPipeline_ComposesTheSameBehavioursInTheSameOrder()
    {
        var services = new ServiceCollection();
        IngestionPipelineBuilder? captured = null;
        services.AddRagNet(ingestion: builder => captured = builder);

        Assert.NotNull(captured);
        var actual = captured.GetBehaviorTypes().Select(static t => t.Name).ToArray();

        Assert.Equal(DefaultIngestionOrder, actual);
    }

    /// <summary>
    /// Resolves the behaviour order through the real container — <c>AddRagNet</c> registers the
    /// <see cref="RetrievalPipelineBuilder"/> singleton it builds the pipeline from, so this is
    /// the same instance a user's composition observes.
    /// </summary>
    private static string[] ResolveRetrievalBehaviourOrder(ServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<RetrievalPipelineBuilder>()
            .GetBehaviorTypes()
            .Select(static t => t.Name)
            .ToArray();
    }

    /// <summary>Never invoked — present only so <c>ConfigureResilience</c> has a surface to wrap.</summary>
    private sealed class StubEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                [new Embedding<float>(new float[] { 1f })]));

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }
}
