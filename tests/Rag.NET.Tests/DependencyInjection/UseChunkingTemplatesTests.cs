using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Chunking.Templates;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseChunkingTemplatesTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IChatClient>());
        return services;
    }

    [Fact]
    public void UseLegalChunking_RegistersIDocumentChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseLegalChunking()).BuildServiceProvider();
        Assert.IsType<LegalChunkingStrategy>(sp.GetRequiredService<IDocumentChunkingStrategy>());
    }

    [Fact]
    public void UseBookChunking_RegistersIDocumentChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseBookChunking()).BuildServiceProvider();
        Assert.IsType<BookChunkingStrategy>(sp.GetRequiredService<IDocumentChunkingStrategy>());
    }

    [Fact]
    public void UseAcademicPaperChunking_RegistersIDocumentChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseAcademicPaperChunking()).BuildServiceProvider();
        Assert.IsType<AcademicPaperChunkingStrategy>(sp.GetRequiredService<IDocumentChunkingStrategy>());
    }

    [Fact]
    public void UseQAPairsChunking_RegistersIDocumentChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseQAPairsChunking()).BuildServiceProvider();
        Assert.IsType<QAPairsChunkingStrategy>(sp.GetRequiredService<IDocumentChunkingStrategy>());
    }

    [Fact]
    public void UseQAPairsChunking_RegistersQAPairsDocumentParser()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseQAPairsChunking()).BuildServiceProvider();
        Assert.NotNull(sp.GetService<QAPairsDocumentParser>());
    }

    [Fact]
    public void UseEmailChunking_RegistersEmailDocumentParser()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseEmailChunking()).BuildServiceProvider();
        Assert.NotNull(sp.GetService<EmailDocumentParser>());
    }

    [Fact]
    public void UseResumeChunking_RegistersIDocumentChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseResumeChunking()).BuildServiceProvider();
        Assert.IsType<ResumeChunkingStrategy>(sp.GetRequiredService<IDocumentChunkingStrategy>());
    }

    [Fact]
    public void UseLegalChunking_CustomOptions_Applied()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseLegalChunking(o => o.MaxDepth = 2))
            .BuildServiceProvider();
        Assert.Equal(2, sp.GetRequiredService<LegalChunkingOptions>().MaxDepth);
    }
}
