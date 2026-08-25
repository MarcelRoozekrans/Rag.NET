using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Parsers;
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

    /// <summary><c>Contains</c> is never called anywhere in this repo's own source, but it is
    /// permanent surface on a published package and needs its own coverage.</summary>
    [Fact]
    public void Contains_WithARegisteredName_ReturnsTrue()
    {
        var services = new ServiceCollection();
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IRagPipelineFactory>();

        Assert.True(factory.Contains("docs"));
    }

    /// <summary>The converse of the test above — an unregistered name reports false, not a throw.</summary>
    [Fact]
    public void Contains_WithAnUnregisteredName_ReturnsFalse()
    {
        var services = new ServiceCollection();
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IRagPipelineFactory>();

        Assert.False(factory.Contains("absent"));
    }

    /// <summary>Two names get two pipelines, each reaching its own store.</summary>
    /// <remarks>
    /// <para>The claim the feature exists to make. Without it nothing else here is verified.</para>
    /// <para>
    /// Asserts identity against each name's own store, not merely that the two <c>IRagPipeline</c>
    /// instances differ: two child providers always construct distinct <c>RagPipeline</c> objects
    /// regardless of which store ended up where, so <c>Assert.NotSame</c> on the pipelines alone
    /// cannot fail for anything to do with store isolation — swapping "docs" and "support"'s store
    /// registrations would leave that assertion green. Resolving each store through the matching
    /// name's own provider is the assertion that actually pins isolation.
    /// </para>
    /// </remarks>
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
        var factory = (RagPipelineFactory)provider.GetRequiredService<IRagPipelineFactory>();

        Assert.NotSame(factory.Get("docs"), factory.Get("support"));
        Assert.Same(docsStore, factory.ProviderFor("docs").GetRequiredService<IVectorStore>());
        Assert.Same(supportStore, factory.ProviderFor("support").GetRequiredService<IVectorStore>());
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

    /// <summary>C1: a shared block that pulls in a whole HttpClient family — open generics and
    /// non-singletons included — must not crash the first factory resolution.</summary>
    /// <remarks>
    /// Regression test for the crash reproduced against real
    /// Microsoft.Extensions.DependencyInjection 10.0.11: the old <c>BuildFactory</c> called
    /// <c>rootProvider.GetRequiredService(serviceType)</c> on every declared-shared type without
    /// filtering, and <c>AddHttpClient()</c> alone declares 44 descriptors (confirmed by direct
    /// inspection) — several open generics (<c>IOptions&lt;&gt;</c>, <c>ILogger&lt;&gt;</c>,
    /// <c>ITypedHttpClientFactory&lt;&gt;</c>) and non-singletons (a transient <c>HttpClient</c>, a
    /// scoped <c>IOptionsSnapshot&lt;&gt;</c>). Forwarding <c>IOptions&lt;&gt;</c> threw
    /// <c>ArgumentException: Implementation type 'UnnamedOptionsManager&lt;TOptions&gt;' can't be
    /// converted to service type 'IOptions&lt;TOptions&gt;'</c> on the very first
    /// <c>GetRequiredService&lt;IRagPipelineFactory&gt;()</c> — before any pipeline was even
    /// resolved. Against the old code this test fails on <c>services.BuildServiceProvider()</c>'s
    /// <c>GetRequiredService&lt;IRagPipelineFactory&gt;()</c> call inside
    /// <c>TryAddSingleton&lt;IRagPipelineFactory&gt;</c>'s factory.
    /// </remarks>
    [Fact]
    public void ASharedHttpClientFamily_DoesNotThrowWhenTheFactoryIsResolved()
    {
        var services = new ServiceCollection();
        services.AddRagNetShared(rag =>
        {
            rag.Services.AddSingleton(Embedder());
            rag.Services.AddHttpClient();
        });
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IRagPipelineFactory>();

        Assert.NotNull(factory.Get("docs"));
    }

    /// <summary>C2: sharing a multi-registered service forwards every instance and keeps none of
    /// the child's own registrations for that type.</summary>
    /// <remarks>
    /// <para>
    /// Regression test for <c>ServiceCollectionDescriptorExtensions.Replace</c> removing only the
    /// <em>first</em> matching descriptor. <c>IDocumentParser</c> is exactly this shape in
    /// production — <c>TextDocumentParser</c> and <c>MarkdownDocumentParser</c> both carry
    /// <c>[Singleton(As = typeof(IDocumentParser), AllowMultiple = true)]</c>, so every child
    /// starts with two registrations before forwarding runs.
    /// </para>
    /// <para>
    /// Against the old code, sharing two parsers here would delete only the first built-in
    /// registration (leaving the other built-in behind) and forward only the last root
    /// registration resolved via <c>GetRequiredService</c> — leaving the child with one built-in
    /// parser plus one shared parser, silently wrong in both directions. The fix removes all of
    /// the child's own registrations for the type and adds every root instance in their place.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASharedMultiRegisteredService_ForwardsAllInstancesAndKeepsNoneOfTheChildsOwn()
    {
        var pdfParser = Substitute.For<IDocumentParser>();
        var csvParser = Substitute.For<IDocumentParser>();

        var services = new ServiceCollection();
        services.AddRagNetShared(rag =>
        {
            rag.Services.AddSingleton(Embedder());
            rag.Services.AddSingleton(pdfParser);
            rag.Services.AddSingleton(csvParser);
        });
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();
        var factory = (RagPipelineFactory)provider.GetRequiredService<IRagPipelineFactory>();

        var docsParsers = factory.ProviderFor("docs").GetServices<IDocumentParser>().ToArray();

        Assert.Equal(2, docsParsers.Length);
        Assert.Contains(pdfParser, docsParsers);
        Assert.Contains(csvParser, docsParsers);
        Assert.DoesNotContain(docsParsers, parser => parser is TextDocumentParser);
        Assert.DoesNotContain(docsParsers, parser => parser is MarkdownDocumentParser);
    }
}
