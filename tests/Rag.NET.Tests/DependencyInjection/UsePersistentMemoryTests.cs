using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Memory;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UsePersistentMemoryTests
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
    public void UsePersistentMemory_WrapsWithDecorator()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseConversationMemory(configure: mem => mem.UsePersistentMemory()))
            .BuildServiceProvider();

        Assert.IsType<PersistentConversationMemory>(sp.GetRequiredService<IConversationMemory>());
    }

    [Fact]
    public void UseConversationMemory_WithoutConfigure_RegistersConversationMemoryPipeline()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseConversationMemory())
            .BuildServiceProvider();

        Assert.IsType<ConversationMemoryPipeline>(sp.GetRequiredService<IConversationMemory>());
    }

    [Fact]
    public void UsePersistentMemory_DefaultOptions_TopKIsThree()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseConversationMemory(configure: mem => mem.UsePersistentMemory()))
            .BuildServiceProvider();

        Assert.Equal(3, sp.GetRequiredService<PersistentMemoryOptions>().TopK);
    }

    [Fact]
    public void UsePersistentMemory_CustomOptions_Registered()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseConversationMemory(
                configure: mem => mem.UsePersistentMemory(new PersistentMemoryOptions { TopK = 5 })))
            .BuildServiceProvider();

        Assert.Equal(5, sp.GetRequiredService<PersistentMemoryOptions>().TopK);
    }
}
