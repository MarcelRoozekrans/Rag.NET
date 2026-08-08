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
    public void UseQAPairsChunking_RegistersIDocumentParser()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseQAPairsChunking()).BuildServiceProvider();
        Assert.IsType<QAPairsDocumentParser>(sp.GetRequiredService<IDocumentParser>());
    }

    /// <summary>
    /// Task 5 retired <c>EmailTemplateDocumentParser</c>: this call now registers only the chunking
    /// strategy, never a parser. <c>.eml</c> ingestion alongside it needs
    /// <c>Rag.NET.Parsers.Email</c> added separately (<c>AddEmailParser()</c>).
    /// </summary>
    [Fact]
    public void UseEmailChunking_RegistersNoParserClaim()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseEmailChunking()).BuildServiceProvider();
        Assert.DoesNotContain(
            sp.GetServices<ParserClaim>(),
            c => string.Equals(c.RegistrationMethod, "UseEmailChunking()", StringComparison.Ordinal));
    }

    [Fact]
    public void UseEmailChunking_RegistersIDocumentChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseEmailChunking()).BuildServiceProvider();
        Assert.IsType<EmailChunkingStrategy>(sp.GetRequiredService<IDocumentChunkingStrategy>());
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
