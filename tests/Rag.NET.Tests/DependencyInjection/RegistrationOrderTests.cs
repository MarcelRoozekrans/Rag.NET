using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

/// <summary>
/// Whether the AI services must be registered before <c>AddRagNet</c>, as the quickstart says.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <c>docs/getting-started.md</c> instructs the reader to "Register them
/// before calling <c>AddRagNet</c>". Nothing was found that would make that true: every consumption
/// of <see cref="IChatClient"/> and <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> in the
/// registration path goes through <c>sp.GetService</c> or <c>sp.GetRequiredService</c> inside a
/// factory lambda, which runs at resolution time rather than registration time.
/// </para>
/// <para>
/// <b>That reading is not evidence, which is the point of the test.</b> Phase 6.2.43 was scoped
/// from a reading of the same kind, and two reasonable greps of the extension surface disagreed by
/// an order of magnitude. So the claim is settled by executing both orders rather than by arguing
/// from the source. Either outcome is a result: if order is irrelevant the documentation sentence
/// is deleted, and if it is not, the sentence is right and the constraint deserves a better
/// explanation than it currently has.
/// </para>
/// <para>
/// <b>What this does not claim.</b> It covers the registrations the quickstart performs. It says
/// nothing about a provider whose own <c>Add…</c> helper eagerly reads the container, and nothing
/// about <c>AddRagNetPipelineFromConfiguration</c>, which registers both halves itself and so
/// cannot express the wrong order.
/// </para>
/// </remarks>
public class RegistrationOrderTests
{
    private static IEmbeddingGenerator<string, Embedding<float>> Embedder() =>
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

    private static IChatClient ChatClient() => Substitute.For<IChatClient>();

    /// <summary>The documented order: AI services first, then <c>AddRagNet</c>.</summary>
    [Fact]
    public void TheDocumentedOrderResolvesThePipeline()
    {
        var services = new ServiceCollection();

        services.AddSingleton(ChatClient());
        services.AddSingleton(Embedder());
        services.AddRagNet(rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IRagPipeline>());
    }

    /// <summary>
    /// The order the documentation warns against: <c>AddRagNet</c> first, AI services after.
    /// </summary>
    /// <remarks>
    /// If this passes, the quickstart's instruction describes a constraint that does not exist.
    /// </remarks>
    [Fact]
    public void TheReversedOrderAlsoResolvesThePipeline()
    {
        var services = new ServiceCollection();

        services.AddRagNet(rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));
        services.AddSingleton(ChatClient());
        services.AddSingleton(Embedder());

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IRagPipeline>());
    }

    /// <summary>
    /// Both orders resolve the same instances, not merely something non-null.
    /// </summary>
    /// <remarks>
    /// Resolving a pipeline in each order proves neither throws. It does not prove they agree —
    /// a registration-time read could capture a different instance and still produce a pipeline.
    /// This asserts the container hands back the exact objects registered, in both orders, which
    /// is the property the documentation sentence is really about.
    /// </remarks>
    [Fact]
    public void BothOrdersResolveTheSameRegisteredInstances()
    {
        var documentedChat = ChatClient();
        var documentedEmbedder = Embedder();
        var documented = new ServiceCollection();
        documented.AddSingleton(documentedChat);
        documented.AddSingleton(documentedEmbedder);
        documented.AddRagNet(rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        var reversedChat = ChatClient();
        var reversedEmbedder = Embedder();
        var reversed = new ServiceCollection();
        reversed.AddRagNet(rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));
        reversed.AddSingleton(reversedChat);
        reversed.AddSingleton(reversedEmbedder);

        using var documentedProvider = documented.BuildServiceProvider();
        using var reversedProvider = reversed.BuildServiceProvider();

        Assert.Same(documentedChat, documentedProvider.GetRequiredService<IChatClient>());
        Assert.Same(
            documentedEmbedder,
            documentedProvider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());

        Assert.Same(reversedChat, reversedProvider.GetRequiredService<IChatClient>());
        Assert.Same(
            reversedEmbedder,
            reversedProvider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
    }
}
