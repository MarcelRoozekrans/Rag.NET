using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Raptor.Store;
using Xunit;

namespace Rag.NET.Raptor.Tests;

/// <summary>
/// These tests are about <c>UseRaptor</c>'s registration mechanics, not <c>TreeScope</c>, so every
/// call here sets <c>TreeScope = PerDocument</c> explicitly — the default is now <c>Corpus</c>,
/// which requires <c>leafStorePath</c> and would otherwise fail every one of these at the
/// <c>UseRaptor</c> line before it got anywhere near what each test actually checks (#331).
/// </summary>
public class RagBuilderExtensionsTests
{
    [Fact]
    public void UseRaptor_RegistersOptionsAsSingleton()
    {
        var builder = ConfiguredRagBuilder.Create();
        var services = builder.Services;

        builder.UseRaptor(o => o.TreeScope = RaptorTreeScope.PerDocument);

        Assert.Contains(services, d => d.ServiceType == typeof(RaptorOptions));
        Assert.Contains(services, d => d.ServiceType == typeof(RaptorRetrievalOptions));
    }

    [Fact]
    public void UseRaptor_WithConfigure_AppliesOptions()
    {
        var builder = ConfiguredRagBuilder.Create();
        var services = builder.Services;

        builder.UseRaptor(o =>
        {
            o.TreeScope = RaptorTreeScope.PerDocument;
            o.MinChunksForRaptor = 42;
        });

        var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<RaptorOptions>();
        Assert.Equal(42, opts.MinChunksForRaptor);
    }

    [Fact]
    public void UseRaptor_WithRetrievalConfigure_AppliesOptions()
    {
        var builder = ConfiguredRagBuilder.Create();
        var services = builder.Services;

        builder.UseRaptor(
            o => o.TreeScope = RaptorTreeScope.PerDocument,
            retrieval: o => o.Mode = RaptorRetrievalMode.Boost);

        var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<RaptorRetrievalOptions>();
        Assert.Equal(RaptorRetrievalMode.Boost, opts.Mode);
    }

    [Fact]
    public void UseRaptor_RegistersIngestionBehavior()
    {
        var builder = ConfiguredRagBuilder.Create();
        var services = builder.Services;

        builder.UseRaptor(o => o.TreeScope = RaptorTreeScope.PerDocument);

        Assert.Contains(services, d => d.ServiceType == typeof(RaptorIngestionBehavior));
    }

    [Fact]
    public void UseRaptor_ReturnsBuilderForChaining()
    {
        var builder = ConfiguredRagBuilder.Create();

        var result = builder.UseRaptor(o => o.TreeScope = RaptorTreeScope.PerDocument);

        Assert.Same(builder, result);
    }

    [Fact]
    public void UseRaptor_RegistersRetrievalBehavior()
    {
        var builder = ConfiguredRagBuilder.Create();
        var services = builder.Services;

        builder.UseRaptor(o => o.TreeScope = RaptorTreeScope.PerDocument);

        Assert.Contains(services, d => d.ServiceType == typeof(RaptorRetrievalBehavior));
    }

    // ---- Corpus scope requires leafStorePath (#331) — the breaking change's own guard, I5 ----

    [Fact]
    public void UseRaptor_CorpusScopeWithoutLeafStorePath_ThrowsAtRegistration()
    {
        // TreeScope defaults to Corpus, so calling UseRaptor with no leafStorePath at all
        // exercises the default a caller who upgrades and changes nothing actually hits.
        var builder = ConfiguredRagBuilder.Create();

        Assert.Throws<ArgumentException>(() => builder.UseRaptor());
    }

    [Fact]
    public void UseRaptor_CorpusScopeWithoutLeafStorePath_MessageNamesLeafStorePath()
    {
        var builder = ConfiguredRagBuilder.Create();

        var ex = Assert.Throws<ArgumentException>(() => builder.UseRaptor());

        Assert.Contains("leafStorePath", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UseRaptor_WithLeafStorePath_RegistersLeafStoreAndTreeRebuilder()
    {
        var builder = ConfiguredRagBuilder.Create();
        var services = builder.Services;
        // RaptorIngestionBehavior and RaptorTreeRebuilder both resolve IChatClient,
        // IEmbeddingGenerator and IVectorStore from the container — AddRagNet does not register
        // these itself, so GetRequiredService<RaptorTreeRebuilder>() below needs them present to
        // prove the registration actually resolves, not merely that it exists in the collection.
        services.AddSingleton(Substitute.For<IChatClient>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IVectorStore>());

        builder.UseRaptor(leafStorePath: ":memory:");

        await using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IRaptorLeafStore>());
        Assert.NotNull(provider.GetRequiredService<RaptorTreeRebuilder>());
    }

    // ---- StoreLeafChunks has no meaning under Corpus scope, I1 ----

    [Fact]
    public void UseRaptor_CorpusScopeWithStoreLeafChunksFalse_ThrowsAtRegistration()
    {
        // Under Corpus scope leaf chunks live in the leaf store, not the ingestion context, so
        // StoreLeafChunks = false has nothing to act on and used to be silently ignored — the
        // only existing test for the option set TreeScope = PerDocument, so it could never catch
        // this. A leafStorePath is supplied so the failure asserted below is this guard's, not
        // the Corpus-requires-leafStorePath one above it.
        var builder = ConfiguredRagBuilder.Create();

        var ex = Assert.Throws<ArgumentException>(() => builder.UseRaptor(
            o =>
            {
                o.TreeScope = RaptorTreeScope.Corpus;
                o.StoreLeafChunks = false;
            },
            leafStorePath: ":memory:"));

        Assert.Contains("StoreLeafChunks", ex.Message, StringComparison.Ordinal);
        Assert.Contains("TreeScope", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseRaptor_CorpusScopeWithStoreLeafChunksTrue_IsAccepted()
    {
        // The default (StoreLeafChunks = true) must keep working under Corpus scope — this guard
        // should reject only the one combination that has no meaning, not StoreLeafChunks itself.
        var builder = ConfiguredRagBuilder.Create();
        var services = builder.Services;

        builder.UseRaptor(o => o.TreeScope = RaptorTreeScope.Corpus, leafStorePath: ":memory:");

        var options = services.BuildServiceProvider().GetRequiredService<RaptorOptions>();
        Assert.True(options.StoreLeafChunks);
    }
}
