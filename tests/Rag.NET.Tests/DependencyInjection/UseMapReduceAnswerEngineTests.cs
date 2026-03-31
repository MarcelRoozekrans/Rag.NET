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

public class UseMapReduceAnswerEngineTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<ILogger<MapReduceAnswerEngine>>());
        services.AddSingleton(Substitute.For<IChatClient>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IVectorStore>());
        return services;
    }

    [Fact]
    public void UseMapReduceAnswerEngine_RegistersIAnswerEngine()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseMapReduceAnswerEngine()).BuildServiceProvider();
        Assert.IsType<MapReduceAnswerEngine>(sp.GetRequiredService<IAnswerEngine>());
    }

    [Fact]
    public void WithoutUseMapReduceAnswerEngine_DefaultIsChatAnswerEngine()
    {
        var sp = BaseServices().AddRagNet().BuildServiceProvider();
        Assert.Null(sp.GetService<IAnswerEngine>());
    }
}
