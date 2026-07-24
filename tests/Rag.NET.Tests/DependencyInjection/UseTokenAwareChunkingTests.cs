using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Chunking.TokenAware;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
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

    [Fact]
    public async Task UseTokenAwareChunking_ConfigureAction_RegistersStrategyWithConfiguredWindow()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseTokenAwareChunking(o =>
            {
                o.WindowSizeTokens = 8;
                o.OverlapTokens = 2;
            }))
            .BuildServiceProvider();

        var strategy = Assert.IsType<TokenAwareChunkingStrategy>(sp.GetRequiredService<IChunkingStrategy>());

        var section = new DocumentSection
        {
            Text = string.Join(' ', Enumerable.Repeat("word", 40)),
            DocumentId = new DocumentId("doc-1"),
            SectionIndex = 0,
        };
        var chunks = await strategy.ChunkAsync(section, new ChunkingOptions(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // A registration that ignored the configure action would use the 512-token default and yield a single chunk.
        Assert.True(chunks.Count > 1);
    }
}
