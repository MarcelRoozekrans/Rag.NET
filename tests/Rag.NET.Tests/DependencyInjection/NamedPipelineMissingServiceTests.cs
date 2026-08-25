using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

/// <summary>
/// What a named pipeline says when a service it needs is not reachable from its container (#390).
/// </summary>
/// <remarks>
/// <para>
/// The container's own message names the missing type and stops. That sent a reporter looking for a
/// registration which, in the shape that produces this most often, is already present — on the root
/// collection, where no named pipeline can see it. These pin that the failure now says where the
/// registration has to go instead.
/// </para>
/// <para>
/// The forwarding rule itself is unchanged and is not a bug: only what <c>AddRagNetShared</c>
/// declared is forwarded, deliberately, so a child does not inherit the host's logging,
/// configuration and HttpClients. <see cref="Get_WhenTheServiceIsOnlyOnTheRootCollection_StillFails"/>
/// is that rule asserted, so a later change that quietly widened forwarding would be caught here
/// rather than discovered as a container-shape surprise.
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
    public void Get_WhenNothingProvidesTheService_NamesWhereToRegisterIt()
    {
        // #390's shape: a store, but no embedding generator anywhere.
        var services = new ServiceCollection();
        services.AddRagNet("abc", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IRagPipelineFactory>();

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Get("abc"));

        Assert.Contains("'abc'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AddRagNetShared", ex.Message, StringComparison.Ordinal);
        // The empty case is the informative one: it says the shared block declared nothing, which is
        // the actual state a reader needs to see.
        Assert.Contains("AddRagNetShared was never called", ex.Message, StringComparison.Ordinal);

        // The container's own message survives as the inner exception, so the missing type is still
        // named — this adds to the diagnosis rather than replacing it.
        var inner = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("IEmbeddingGenerator", inner.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_WhenTheServiceIsOnlyOnTheRootCollection_StillFails()
    {
        // Registered, visible in the container, and unreachable from a named pipeline. This is the
        // case that produced #390's confusion, and the rule is deliberate — so it is asserted, not
        // fixed.
        var services = new ServiceCollection();
        services.AddSingleton(Embedder());
        services.AddRagNet("abc", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();

        // The root really does have it; the child really does not.
        Assert.NotNull(provider.GetService<IEmbeddingGenerator<string, Embedding<float>>>());

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IRagPipelineFactory>().Get("abc"));

        Assert.Contains("Services registered directly on IServiceCollection", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_WhenTheSharedBlockDeclaredIt_Succeeds()
    {
        // The fix the message points at, asserted end to end — otherwise the guidance could name a
        // remedy that does not work.
        var services = new ServiceCollection();
        services.AddRagNetShared(rag => rag.Services.AddSingleton(Embedder()));
        services.AddRagNet("abc", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IRagPipelineFactory>().Get("abc"));
    }

    [Fact]
    public void Get_WhenTheSharedBlockDeclaredSomethingElse_ListsWhatItDeclared()
    {
        // A shared block exists but does not carry the needed service. Naming what it *does* carry
        // is what distinguishes "you forgot the shared block" from "your shared block is missing
        // this one thing", which are different mistakes with different fixes.
        var services = new ServiceCollection();
        services.AddRagNetShared(rag => rag.Services.AddSingleton(Substitute.For<IBm25Index>()));
        services.AddRagNet("abc", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IRagPipelineFactory>().Get("abc"));

        Assert.Contains("IBm25Index", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AddRagNetShared was never called", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_WhenTheServiceIsInTheNamedBlock_Succeeds()
    {
        // The other remedy the message names.
        var services = new ServiceCollection();
        services.AddRagNet("abc", rag =>
        {
            rag.Services.AddSingleton(Embedder());
            rag.Services.AddSingleton(Substitute.For<IVectorStore>());
        });

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IRagPipelineFactory>().Get("abc"));
    }

    [Fact]
    public void Get_WithAnUnknownName_StillThrowsArgumentException()
    {
        // The new catch wraps InvalidOperationException only. An unknown name is an ArgumentException
        // and must keep its type and its parameter name, or callers matching on it break.
        var services = new ServiceCollection();
        services.AddRagNetShared(rag => rag.Services.AddSingleton(Embedder()));
        services.AddRagNet("abc", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<ArgumentException>(
            () => provider.GetRequiredService<IRagPipelineFactory>().Get("nope"));

        Assert.Equal("name", ex.ParamName);
    }
}
