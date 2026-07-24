using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UsePropositionChunkingTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IChatClient>());
        return services;
    }

    [Fact]
    public void UsePropositionChunking_RegistersIChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UsePropositionChunking()).BuildServiceProvider();
        Assert.IsType<PropositionChunkingStrategy>(sp.GetRequiredService<IChunkingStrategy>());
    }

    [Fact]
    public void UsePropositionChunking_RegistersIDocumentChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UsePropositionChunking()).BuildServiceProvider();
        Assert.IsType<PropositionChunkingStrategy>(sp.GetRequiredService<IDocumentChunkingStrategy>());
    }

    [Fact]
    public void UsePropositionChunking_BothInterfacesResolveToSameInstance()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UsePropositionChunking()).BuildServiceProvider();
        var chunking = sp.GetRequiredService<IChunkingStrategy>();
        var docChunking = sp.GetRequiredService<IDocumentChunkingStrategy>();

        Assert.Same(chunking, docChunking);
    }

    [Fact]
    public void UsePropositionChunking_ConfigureIsApplied()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UsePropositionChunking(o => o.MaxPassageTokens = 500))
            .BuildServiceProvider();

        Assert.Equal(500, sp.GetRequiredService<Rag.NET.Models.Options.PropositionChunkingOptions>().MaxPassageTokens);
    }

    [Fact]
    public void UsePropositionChunking_InvalidMaxPassageTokens_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            BaseServices().AddRagNet(rag => rag.UsePropositionChunking(o => o.MaxPassageTokens = 0)));
        Assert.Contains("MaxPassageTokens", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UsePropositionChunking_InvalidMaxPropositionsPerPassage_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            BaseServices().AddRagNet(rag => rag.UsePropositionChunking(o => o.MaxPropositionsPerPassage = -1)));
        Assert.Contains("MaxPropositionsPerPassage", ex.Message, StringComparison.Ordinal);
        Assert.Contains("-1", ex.Message, StringComparison.Ordinal);
    }
}
