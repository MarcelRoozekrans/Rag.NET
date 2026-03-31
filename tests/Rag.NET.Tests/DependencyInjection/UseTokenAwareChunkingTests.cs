using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Chunking.TokenAware;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseTokenAwareChunkingTests
{
    [Fact]
    public void UseTokenAwareChunking_RegistersIChunkingStrategy()
    {
        var services = new ServiceCollection();
        services.AddRagNet(rag => rag.UseTokenAwareChunking());

        var sp = services.BuildServiceProvider();
        var strategy = sp.GetRequiredService<IChunkingStrategy>();

        Assert.IsType<TokenAwareChunkingStrategy>(strategy);
    }

    [Fact]
    public void UseTokenAwareChunking_CustomModel_RegistersWithThatModel()
    {
        var services = new ServiceCollection();
        services.AddRagNet(rag => rag.UseTokenAwareChunking("gpt-3.5-turbo"));

        var sp = services.BuildServiceProvider();
        var strategy = sp.GetRequiredService<IChunkingStrategy>();

        Assert.IsType<TokenAwareChunkingStrategy>(strategy);
    }
}
