using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Security;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseSecurityTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IChatClient>());
        services.AddSingleton(Substitute.For<IVectorStore>());
        return services;
    }

    [Fact]
    public void UseChunkSanitiser_RegistersIChunkSanitiser()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseChunkSanitiser()).BuildServiceProvider();
        Assert.IsType<RegexChunkSanitiser>(sp.GetRequiredService<IChunkSanitiser>());
    }

    [Fact]
    public void UseChunkSanitiser_MultipleRegistrations_AllResolvable()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseChunkSanitiser().UseLlmChunkSanitiser())
            .BuildServiceProvider();
        var all = sp.GetServices<IChunkSanitiser>().ToList();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void UseQuerySanitiser_RegistersIQuerySanitiser()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseQuerySanitiser()).BuildServiceProvider();
        Assert.IsType<RegexQuerySanitiser>(sp.GetRequiredService<IQuerySanitiser>());
    }

    [Fact]
    public void UseQuerySanitiser_WrapsIRagPipelineWithDecorator()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseQuerySanitiser()).BuildServiceProvider();
        Assert.IsType<QuerySanitiserPipelineDecorator>(sp.GetRequiredService<IRagPipeline>());
    }

    [Fact]
    public void UseRetrievalGuard_RegistersIRetrievalGuard()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseRetrievalGuard()).BuildServiceProvider();
        Assert.IsType<RegexRetrievalGuard>(sp.GetRequiredService<IRetrievalGuard>());
    }

    [Fact]
    public void UseTrustLevelGuard_RegistersIRetrievalGuard()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseTrustLevelGuard()).BuildServiceProvider();
        Assert.Contains(sp.GetServices<IRetrievalGuard>(), g => g is TrustLevelRetrievalGuard);
    }

    [Fact]
    public void UsePromptHardening_RegistersPromptHardeningOptions()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UsePromptHardening()).BuildServiceProvider();
        var opts = sp.GetRequiredService<PromptHardeningOptions>();
        Assert.NotEmpty(opts.SystemPrefix);
    }
}
