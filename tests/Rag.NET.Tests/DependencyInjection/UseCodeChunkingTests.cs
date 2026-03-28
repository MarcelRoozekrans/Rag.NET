using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.DependencyInjection;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseCodeChunkingTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IVectorStore>());
        return services;
    }

    [Fact]
    public void UseCodeChunking_IChunkingStrategyIsCodeChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseCodeChunking()).BuildServiceProvider();
        Assert.IsType<CodeChunkingStrategy>(sp.GetRequiredService<IChunkingStrategy>());
    }

    [Fact]
    public void UseCodeChunking_DefaultOptions_LanguageIsNull()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseCodeChunking()).BuildServiceProvider();
        Assert.Null(sp.GetRequiredService<CodeChunkingOptions>().Language);
    }

    [Fact]
    public void UseCodeChunking_WithLanguage_OptionsRegistered()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseCodeChunking(new CodeChunkingOptions { Language = "python" }))
            .BuildServiceProvider();
        Assert.Equal("python", sp.GetRequiredService<CodeChunkingOptions>().Language);
    }

    [Fact]
    public void UseCodeChunking_UnrecognisedLanguage_ThrowsImmediately()
    {
        Assert.Throws<ArgumentException>(() =>
            BaseServices().AddRagNet(rag => rag.UseCodeChunking(new CodeChunkingOptions { Language = "brainfuck" })));
    }
}
