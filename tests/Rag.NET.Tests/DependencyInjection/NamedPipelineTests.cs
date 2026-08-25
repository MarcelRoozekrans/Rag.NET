using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public sealed class NamedPipelineTests
{
    private static IEmbeddingGenerator<string, Embedding<float>> Embedder()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        return embedder;
    }

    [Fact]
    public void Get_ReturnsTheSameInstanceEachTime()
    {
        var services = new ServiceCollection();
        services.AddRagNetShared(rag => rag.Services.AddSingleton(Embedder()));
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IRagPipelineFactory>();

        Assert.Same(factory.Get("docs"), factory.Get("docs"));
    }

    [Fact]
    public void Get_WithAnUnknownName_Throws()
    {
        var services = new ServiceCollection();
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IRagPipelineFactory>();

        _ = Assert.Throws<ArgumentException>(() => factory.Get("absent"));
    }

    /// <summary>Two names get two pipelines, each reaching its own store.</summary>
    /// <remarks>The claim the feature exists to make. Without it nothing else here is verified.</remarks>
    [Fact]
    public void TwoNames_GetSeparatePipelinesWithSeparateStores()
    {
        var docsStore = Substitute.For<IVectorStore>();
        var supportStore = Substitute.For<IVectorStore>();

        var services = new ServiceCollection();
        services.AddRagNetShared(rag => rag.Services.AddSingleton(Embedder()));
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(docsStore));
        services.AddRagNet("support", rag => rag.Services.AddSingleton(supportStore));

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IRagPipelineFactory>();

        Assert.NotSame(factory.Get("docs"), factory.Get("support"));
    }

    /// <summary>A shared service is one instance across every named pipeline.</summary>
    /// <remarks>
    /// <para>
    /// The test that would catch descriptor copying duplicating an ONNX session: a type registration
    /// copied into two child collections constructs two instances, silently.
    /// </para>
    /// <para>
    /// "docs" also registers its own embedder before the shared one is forwarded — the exact
    /// collision <c>Replace</c> (rather than <c>Add</c>) exists to resolve. Resolving through the
    /// child, not the root, is deliberate: the root always holds exactly one registration of a type
    /// nothing else there competes for, so asserting against
    /// <c>provider.GetRequiredService&lt;T&gt;()</c> would pass whether or not forwarding worked.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASharedService_IsTheSameInstanceInEveryNamedPipeline()
    {
        var embedder = Embedder();
        var docsOwnEmbedder = Embedder();
        var services = new ServiceCollection();
        services.AddRagNetShared(rag => rag.Services.AddSingleton(embedder));
        services.AddRagNet("docs", rag =>
        {
            rag.Services.AddSingleton(Substitute.For<IVectorStore>());
            rag.Services.AddSingleton(docsOwnEmbedder);
        });
        services.AddRagNet("support", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();
        var factory = (RagPipelineFactory)provider.GetRequiredService<IRagPipelineFactory>();
        _ = factory.Get("docs");
        _ = factory.Get("support");

        // Resolved through each child, it is the one the root holds — not "docs"'s own registration.
        Assert.Same(embedder, factory.ProviderFor("docs").GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
        Assert.Same(embedder, factory.ProviderFor("support").GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
        Assert.Single(factory.ProviderFor("docs").GetServices<IEmbeddingGenerator<string, Embedding<float>>>());
    }

    /// <summary>A per-pipeline service is NOT shared — the converse of the test above.</summary>
    /// <remarks>
    /// Without this, "everything is shared" would satisfy the sharing test and the isolation the
    /// feature promises would be absent.
    /// </remarks>
    [Fact]
    public void APerPipelineService_IsNotSharedBetweenNames()
    {
        var docsStore = Substitute.For<IVectorStore>();
        var supportStore = Substitute.For<IVectorStore>();

        var services = new ServiceCollection();
        services.AddRagNetShared(rag => rag.Services.AddSingleton(Embedder()));
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(docsStore));
        services.AddRagNet("support", rag => rag.Services.AddSingleton(supportStore));

        using var provider = services.BuildServiceProvider();
        var factory = (RagPipelineFactory)provider.GetRequiredService<IRagPipelineFactory>();
        _ = factory.Get("docs");
        _ = factory.Get("support");

        Assert.NotSame(
            factory.ProviderFor("docs").GetRequiredService<IVectorStore>(),
            factory.ProviderFor("support").GetRequiredService<IVectorStore>());
    }

    /// <summary>The unnamed pipeline and a named one coexist in one container.</summary>
    /// <remarks>
    /// The guarantee §1a of the spec makes: named pipelines are additive, and the documented root
    /// resolves keep working.
    /// </remarks>
    [Fact]
    public void UnnamedAndNamed_CoexistInOneContainer()
    {
        var rootStore = Substitute.For<IVectorStore>();
        var docsStore = Substitute.For<IVectorStore>();

        // Declared through AddRagNetShared (not a plain AddSingleton) because the default
        // retrieval pipeline always includes VectorStoreBehavior, which requires an embedder —
        // "docs" needs one reachable, and only a declared-shared type forwards into its
        // otherwise-isolated collection.
        var services = new ServiceCollection();
        services.AddRagNetShared(rag => rag.Services.AddSingleton(Embedder()));
        services.AddRagNet(rag => rag.Services.AddSingleton(rootStore));
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(docsStore));

        using var provider = services.BuildServiceProvider();

        Assert.Same(rootStore, provider.GetRequiredService<IVectorStore>());
        Assert.NotNull(provider.GetRequiredService<IRagPipeline>());
        Assert.NotNull(provider.GetRequiredService<IRagPipelineFactory>().Get("docs"));
    }

    private sealed class DisposableProbe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    /// <summary>A child never owns what it merely forwards — disposing it must not dispose a
    /// shared service.</summary>
    /// <remarks>
    /// Regression test for a factory-based forwarding descriptor —
    /// <c>ServiceDescriptor.Singleton(type, sp =&gt; sp.GetRequiredService(type))</c> — which the
    /// concrete <c>ServiceProvider</c> captures for disposal in whichever child resolved it,
    /// because a factory call site gives the engine no way to know the instance is owned
    /// elsewhere. Reproduced against real Microsoft.Extensions.DependencyInjection 10.0.11:
    /// disposing either child alone disposed the shared instance as a side effect, and disposing
    /// the second child disposed it again. The fix registers the resolved <b>instance</b> instead
    /// of a factory, which the engine excludes from disposal capture entirely.
    /// </remarks>
    [Fact]
    public void DisposingTheFactory_DoesNotDisposeASharedService()
    {
        var probe = new DisposableProbe();
        var services = new ServiceCollection();
        services.AddRagNetShared(rag => rag.Services.AddSingleton(probe));
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));
        services.AddRagNet("support", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();
        var factory = (RagPipelineFactory)provider.GetRequiredService<IRagPipelineFactory>();

        // Resolve the shared probe through both children, forcing both child providers to build
        // and both to have actually realised the forwarded instance.
        Assert.Same(probe, factory.ProviderFor("docs").GetRequiredService<DisposableProbe>());
        Assert.Same(probe, factory.ProviderFor("support").GetRequiredService<DisposableProbe>());

        factory.Dispose();

        Assert.False(probe.Disposed);
    }
}
