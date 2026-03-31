using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Chunking.CSharp;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseCSharpChunkingTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        return services;
    }

    [Fact]
    public void UseCSharpChunking_RegistersIChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseCSharpChunking()).BuildServiceProvider();
        Assert.IsType<CSharpChunkingStrategy>(sp.GetRequiredService<IChunkingStrategy>());
    }

    [Fact]
    public void UseCSharpChunking_DefaultOptions_IncludePrivateIsFalse()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseCSharpChunking()).BuildServiceProvider();
        Assert.False(sp.GetRequiredService<CSharpChunkingOptions>().IncludePrivateMembers);
    }

    [Fact]
    public void UseCSharpChunking_CustomOptions_Applied()
    {
        var opts = new CSharpChunkingOptions { IncludePrivateMembers = true };
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseCSharpChunking(opts))
            .BuildServiceProvider();
        Assert.True(sp.GetRequiredService<CSharpChunkingOptions>().IncludePrivateMembers);
    }
}
