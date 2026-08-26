using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

/// <summary>
/// A named pipeline inherits the host's root singletons (#390), but must not <b>construct</b> the
/// ones it never asks for (#400).
/// </summary>
public sealed class AmbientForwardingLazinessTests
{
    private static IEmbeddingGenerator<string, Embedding<float>> Embedder()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        return embedder;
    }

    /// <summary>
    /// A service type the library knows nothing about, so the child cannot register it itself.
    /// A built-in-backed type such as the document-parser abstraction would prove nothing here:
    /// the child registers those itself, so they are deliberately never forwarded.
    /// </summary>
    private interface IHostOnlyMultiService;

    private sealed class HostOnlyMultiService : IHostOnlyMultiService;

    private sealed class HostOnlyService
    {
        public static int Constructed;
        public HostOnlyService() => Interlocked.Increment(ref HostOnlyService.Constructed);
    }

    [Fact]
    public void GetName_DoesNotConstructAHostSingletonThePipelineNeverAsksFor()
    {
        // The eager form resolved every eligible root registration to forward it as an instance,
        // so Get(name) constructed the host's container whether the pipeline wanted it or not.
        HostOnlyService.Constructed = 0;

        var services = new ServiceCollection();
        services.AddSingleton(Embedder());
        services.AddSingleton<HostOnlyService>();
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IRagPipelineFactory>();

        Assert.NotNull(factory.Get("docs"));
        Assert.Equal(0, HostOnlyService.Constructed);
    }

    [Fact]
    public void AHostSingletonThatBlocksOnConstruction_DoesNotStallGet()
    {
        // Why the count above matters. A host singleton that blocks — a credential acquiring a
        // token, a client probing an endpoint — used to hang Get(name) for a pipeline that had no
        // use for it. Nothing here ever releases the gate: if Get constructs it, this test hangs
        // until the timeout rather than passing slowly.
        using var neverReleased = new ManualResetEventSlim(false);

        var services = new ServiceCollection();
        services.AddSingleton(Embedder());
        services.AddSingleton(_ =>
        {
            neverReleased.Wait();
            return new HostOnlyService();
        });
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IRagPipelineFactory>();

        Assert.NotNull(factory.Get("docs"));
    }

    [Fact]
    public void AHostSingletonThePipelineDoesAskFor_IsStillTheRootsOwnInstance()
    {
        // Laziness must not become isolation: #390's whole point is that the named pipeline sees
        // the host's registration, and sees the same instance the host does.
        var embedder = Embedder();

        var services = new ServiceCollection();
        services.AddSingleton(embedder);
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();
        var factory = (RagPipelineFactory)provider.GetRequiredService<IRagPipelineFactory>();

        Assert.Same(
            embedder,
            factory.ProviderFor("docs").GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
    }

    [Fact]
    public void AMultiRegisteredHostService_ForwardsEveryRegistration_NotJustTheLast()
    {
        // The eager form resolved GetServices(type) and forwarded each instance. Doing this lazily
        // needs one child descriptor per root descriptor, counted without resolving — get that
        // wrong and a multi-registered type silently arrives truncated to one.
        var first = new HostOnlyMultiService();
        var second = new HostOnlyMultiService();
        var third = new HostOnlyMultiService();

        var services = new ServiceCollection();
        services.AddSingleton(Embedder());
        services.AddSingleton<IHostOnlyMultiService>(first);
        services.AddSingleton<IHostOnlyMultiService>(second);
        services.AddSingleton<IHostOnlyMultiService>(third);
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();
        var factory = (RagPipelineFactory)provider.GetRequiredService<IRagPipelineFactory>();

        var forwarded = factory.ProviderFor("docs").GetServices<IHostOnlyMultiService>().ToList();

        Assert.Equal(3, forwarded.Count);
        Assert.Contains(first, forwarded);
        Assert.Contains(second, forwarded);
        Assert.Contains(third, forwarded);
    }

    [Fact]
    public void TheLastHostRegistrationStillWins_AsItDoesInTheRoot()
    {
        // Forwarding resolves by position rather than via GetRequiredService, so the child's
        // "last one wins" has to land on the same instance the root's does.
        var first = new HostOnlyMultiService();
        var last = new HostOnlyMultiService();

        var services = new ServiceCollection();
        services.AddSingleton(Embedder());
        services.AddSingleton<IHostOnlyMultiService>(first);
        services.AddSingleton<IHostOnlyMultiService>(last);
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();
        var factory = (RagPipelineFactory)provider.GetRequiredService<IRagPipelineFactory>();

        Assert.Same(last, provider.GetRequiredService<IHostOnlyMultiService>());
        Assert.Same(last, factory.ProviderFor("docs").GetRequiredService<IHostOnlyMultiService>());
    }
}
