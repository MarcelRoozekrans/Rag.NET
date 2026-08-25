using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

/// <summary>
/// What a named pipeline can see, and what it says when something is genuinely absent (#390).
/// </summary>
/// <remarks>
/// <para>
/// A named pipeline used to see only its own block plus whatever <c>AddRagNetShared</c> declared.
/// That made the named form see <i>less</i> than the unnamed one for identical registrations: the
/// canonical <c>services.AddChatClient(…)</c> / <c>services.AddEmbeddingGenerator(…)</c> pattern
/// writes to the root collection, which <c>AddRagNet(rag => …)</c> reads and a named pipeline did
/// not. #390 reported exactly that, and <c>Rag.NET.Hosting</c> registers the same way.
/// </para>
/// <para>
/// These pin the three-way rule that replaced it: the named block wins, then the shared block, then
/// the host's own root singletons as a default the pipeline may override.
/// </para>
/// </remarks>
public class NamedPipelineMissingServiceTests
{
    private static IEmbeddingGenerator<string, Embedding<float>> Embedder()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        return embedder;
    }

    [Fact]
    public void Get_WhenTheServiceIsOnlyOnTheRootCollection_InheritsIt()
    {
        // #390's actual shape. This asserted the opposite until the report showed the rule was
        // wrong: the same registration works with AddRagNet(rag => …), so it must work here.
        var services = new ServiceCollection();
        services.AddSingleton(Embedder());
        services.AddRagNet("abc", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IRagPipelineFactory>().Get("abc"));
    }

    [Fact]
    public void Get_TheNamedBlockWins_OverAnAmbientRootRegistration()
    {
        // An ambient root service is a default, not an override — otherwise a pipeline could not
        // choose its own store, which is the entire point of naming one.
        var mine = Substitute.For<IVectorStore>();
        var hosts = Substitute.For<IVectorStore>();

        var services = new ServiceCollection();
        services.AddSingleton(Embedder());
        services.AddSingleton(hosts);
        services.AddRagNet("abc", rag => rag.Services.AddSingleton(mine));

        using var provider = services.BuildServiceProvider();
        var factory = (RagPipelineFactory)provider.GetRequiredService<IRagPipelineFactory>();

        Assert.Same(mine, factory.ProviderFor("abc").GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void Get_TwoNamedPipelines_DoNotInheritEachOthersServices()
    {
        // The isolation guarantee. Each child is built by inner.AddRagNet(configure), so it already
        // registers every service type this library owns and another pipeline's cannot arrive —
        // but a store registered inside one block must not leak either.
        var a = Substitute.For<IVectorStore>();
        var b = Substitute.For<IVectorStore>();

        var services = new ServiceCollection();
        services.AddSingleton(Embedder());
        services.AddRagNet("a", rag => rag.Services.AddSingleton(a));
        services.AddRagNet("b", rag => rag.Services.AddSingleton(b));

        using var provider = services.BuildServiceProvider();
        var factory = (RagPipelineFactory)provider.GetRequiredService<IRagPipelineFactory>();

        Assert.Same(a, factory.ProviderFor("a").GetRequiredService<IVectorStore>());
        Assert.Same(b, factory.ProviderFor("b").GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void Get_TheSharedBlockStillReplacesTheNamedBlock()
    {
        // Unchanged, and deliberately opposite to the ambient rule: declaring a type shared says
        // "one of these for every pipeline", so it overrides rather than defers.
        var shared = Embedder();

        var services = new ServiceCollection();
        services.AddRagNetShared(rag => rag.Services.AddSingleton(shared));
        services.AddRagNet("abc", rag =>
        {
            rag.Services.AddSingleton(Embedder());
            rag.Services.AddSingleton(Substitute.For<IVectorStore>());
        });

        using var provider = services.BuildServiceProvider();
        var factory = (RagPipelineFactory)provider.GetRequiredService<IRagPipelineFactory>();

        Assert.Same(shared, factory.ProviderFor("abc").GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
    }

    [Fact]
    public void Get_WhenNothingAnywhereProvidesIt_NamesAllThreePlaces()
    {
        // The failure that survives the fix: genuinely absent everywhere.
        var services = new ServiceCollection();
        services.AddRagNet("abc", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IRagPipelineFactory>().Get("abc"));

        Assert.Contains("'abc'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AddRagNetShared", ex.Message, StringComparison.Ordinal);
        Assert.Contains("nothing anywhere provides this service", ex.Message, StringComparison.Ordinal);
        // All three places are named, because with inheritance in play the reader has a real choice.
        Assert.Contains("the host", ex.Message, StringComparison.Ordinal);

        var inner = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("IEmbeddingGenerator", inner.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_TheFactoryItselfIsNotForwarded()
    {
        // ForwardAmbientRootServices runs inside the factory that resolves IRagPipelineFactory.
        // Forwarding that type would re-enter it. This fails by hanging or throwing, not by
        // asserting — which is why it exists at all.
        var services = new ServiceCollection();
        services.AddSingleton(Embedder());
        services.AddRagNet("abc", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IRagPipelineFactory>().Get("abc"));
    }

    [Fact]
    public void Get_WithAnUnknownName_StillThrowsArgumentException()
    {
        // The guidance catch takes InvalidOperationException only; an unknown name must keep its
        // type and parameter name, or callers matching on it break.
        var services = new ServiceCollection();
        services.AddSingleton(Embedder());
        services.AddRagNet("abc", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<ArgumentException>(
            () => provider.GetRequiredService<IRagPipelineFactory>().Get("nope"));

        Assert.Equal("name", ex.ParamName);
    }
}
