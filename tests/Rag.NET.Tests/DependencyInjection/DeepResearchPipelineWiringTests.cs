using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

/// <summary>
/// Asserts that <c>UseDeepResearch</c> reaches the <b>pipeline's</b> retrieval path, not only the
/// <see cref="IRetriever"/> registration.
/// </summary>
/// <remarks>
/// <see cref="UseDeepResearchTests"/> already pins that <c>GetRequiredService&lt;IRetriever&gt;()</c>
/// returns the decorator. That is a weaker claim than it looks: a benchmark or an application
/// retrieves through <c>IRagPipeline</c>, and nothing here asserted the pipeline resolves the same
/// instance. Written after a Phase 6.2.1 cell configured deep research, retrieved 300 queries
/// through <c>IRagPipeline</c>, and made <b>zero</b> model calls — the decorator was registered and
/// the pipeline did not use it, which reproduced the dense figure exactly.
/// </remarks>
public class DeepResearchPipelineWiringTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IVectorStore>());
        services.AddSingleton(Substitute.For<IChatClient>());
        return services;
    }

    [Fact]
    public void UseDeepResearch_TheRetrieverRegistrationIsTheDecorator()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseDeepResearch()).BuildServiceProvider();

        Assert.IsType<DeepResearchRetriever>(sp.GetRequiredService<IRetriever>());
    }

    [Fact]
    public void UseDeepResearch_ThePipelineResolvesTheSameDecoratedRetriever()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseDeepResearch()).BuildServiceProvider();

        var registered = sp.GetRequiredService<IRetriever>();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        Assert.IsType<DeepResearchRetriever>(registered);

        // The pipeline holds its retriever privately, so this reaches for it rather than asserting
        // on a public surface that does not exist. If RagPipeline's parameter is ever renamed this
        // fails loudly, which is preferable to silently asserting nothing.
        // RagPipeline takes its retriever as a PRIMARY CONSTRUCTOR parameter, so the backing field
        // the compiler emits is name-mangled ("<retriever>P"). Matching on the mangled spelling
        // would be brittle; matching on the field whose type is IRetriever is not.
        var field = Array.Find(
            pipeline.GetType().GetFields(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            f => typeof(IRetriever).IsAssignableFrom(f.FieldType));

        Assert.True(
            field is not null,
            "RagPipeline no longer holds an IRetriever field; this test can no longer see what " +
            "the pipeline retrieves through and must be rewritten rather than deleted.");

        var used = field!.GetValue(pipeline);

        Assert.True(
            ReferenceEquals(used, registered),
            $"the pipeline retrieves through {used?.GetType().Name ?? "null"} while the container " +
            $"resolves {registered.GetType().Name} for IRetriever. Any Use* decorator is then " +
            "configured, registered, and bypassed by every IRagPipeline call — which is silent.");
    }
}
