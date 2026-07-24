using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseLateChunkingTests
{
    private static IServiceCollection ServicesWithGenerator()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<ITokenEmbeddingGenerator>());
        return services;
    }

    [Fact]
    public void UseLateChunking_BothInterfacesResolveToSameInstance()
    {
        var sp = ServicesWithGenerator().AddRagNet(rag => rag.UseLateChunking()).BuildServiceProvider();

        var chunking = sp.GetRequiredService<IChunkingStrategy>();
        var docChunking = sp.GetRequiredService<IDocumentChunkingStrategy>();

        Assert.IsType<LateChunkingStrategy>(chunking);
        Assert.Same(chunking, docChunking);
    }

    [Fact]
    public void UseLateChunking_ConfigureIsApplied()
    {
        var sp = ServicesWithGenerator()
            .AddRagNet(rag => rag.UseLateChunking(o => o.WindowSizeTokens = 128))
            .BuildServiceProvider();

        Assert.Equal(128, sp.GetRequiredService<Rag.NET.Models.Options.LateChunkingOptions>().WindowSizeTokens);
    }

    [Fact]
    public void UseLateChunking_NoGeneratorRegistered_ResolvingThrows()
    {
        var sp = new ServiceCollection().AddRagNet(rag => rag.UseLateChunking()).BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<IChunkingStrategy>());
    }

    [Fact]
    public void UseLateChunking_InvalidOverlap_ThrowsAtRegistration()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ServicesWithGenerator().AddRagNet(rag => rag.UseLateChunking(o =>
            {
                o.WindowSizeTokens = 32;
                o.OverlapTokens = 32;
            })));
        Assert.Contains("OverlapTokens", ex.Message, StringComparison.Ordinal);
        Assert.Contains("32", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseLateChunking_OptionsGenerator_ResolvesWithoutDiRegisteredGenerator()
    {
        var generator = Substitute.For<ITokenEmbeddingGenerator>();
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseLateChunking(o => o.Generator = generator))
            .BuildServiceProvider();

        Assert.IsType<LateChunkingStrategy>(sp.GetRequiredService<IChunkingStrategy>());
        Assert.Same(sp.GetRequiredService<IChunkingStrategy>(), sp.GetRequiredService<IDocumentChunkingStrategy>());
    }
}
