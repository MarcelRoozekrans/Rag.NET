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
        var sp = new ServiceCollection().AddRagNet(rag => rag.UseTokenAwareChunking()).BuildServiceProvider();
        Assert.IsType<TokenAwareChunkingStrategy>(sp.GetRequiredService<IChunkingStrategy>());
    }

    [Fact]
    public void UseTokenAwareChunking_CustomModel_RegistersWithThatModel()
    {
        var sp = new ServiceCollection().AddRagNet(rag => rag.UseTokenAwareChunking("gpt-3.5-turbo")).BuildServiceProvider();
        Assert.IsType<TokenAwareChunkingStrategy>(sp.GetRequiredService<IChunkingStrategy>());
    }
}
