using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.AnswerEngines;
using Rag.NET.AnswerGeneration;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseRefineAnswerEngineTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<ILogger<RefineAnswerEngine>>());
        services.AddSingleton(Substitute.For<IChatClient>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IVectorStore>());
        return services;
    }

    /// <summary>
    /// The minimum a user actually registers — vector store, embedding generator, chat client —
    /// with no logging at all. A missing logger must degrade to no logging, never to a crash.
    /// </summary>
    private static IServiceCollection ServicesWithoutLogging()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IChatClient>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IVectorStore>());
        return services;
    }

    [Fact]
    public void UseRefineAnswerEngine_WithoutLoggingRegistered_ResolvesPipeline()
    {
        var sp = ServicesWithoutLogging()
            .AddRagNet(rag => rag.UseRefineAnswerEngine())
            .BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<IRagPipeline>());
        Assert.IsType<RefineAnswerEngine>(sp.GetRequiredService<IAnswerEngine>());
    }

    [Fact]
    public void UseRefineAnswerEngine_RegistersIAnswerEngine()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseRefineAnswerEngine()).BuildServiceProvider();
        Assert.IsType<RefineAnswerEngine>(sp.GetRequiredService<IAnswerEngine>());
    }

    [Fact]
    public void WithoutUseRefineAnswerEngine_DefaultIsChatAnswerEngine()
    {
        var sp = BaseServices().AddRagNet().BuildServiceProvider();
        Assert.Null(sp.GetService<IAnswerEngine>());
    }
}
