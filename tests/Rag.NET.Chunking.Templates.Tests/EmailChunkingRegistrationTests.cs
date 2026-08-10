using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Chunking.Templates.Tests;

/// <summary>
/// <c>UseEmailChunking</c> applied its configure callback and registered the options singleton,
/// but nothing resolved it — the strategy chunked headers and attachments unconditionally
/// (issue #108). This pins the wiring: the options a caller configures are the options the
/// resolved strategy filters with.
/// </summary>
public sealed class EmailChunkingRegistrationTests
{
    [Fact]
    public async Task UseEmailChunking_ConfiguredOptions_ReachTheResolvedStrategy()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseEmailChunking(o => o.IncludeHeaders = false))
            .BuildServiceProvider();

        var strategy = sp.GetRequiredService<EmailChunkingStrategy>();
        var chunks = await ChunkAllAsync(strategy);

        Assert.Equal(2, chunks.Count);
        Assert.DoesNotContain(chunks, c => c.Metadata["part"] == "headers");
    }

    [Fact]
    public async Task UseEmailChunking_NoConfigure_ChunksEverySection()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseEmailChunking())
            .BuildServiceProvider();

        var strategy = sp.GetRequiredService<EmailChunkingStrategy>();
        var chunks = await ChunkAllAsync(strategy);

        Assert.Equal(3, chunks.Count);
    }

    private static async Task<List<TextChunk>> ChunkAllAsync(EmailChunkingStrategy strategy)
    {
        var docId = new DocumentId("email.eml");
        var chunks = new List<TextChunk>();
        await foreach (var c in strategy.ChunkDocumentAsync(
            Sections(
                new DocumentSection { Text = "From: a@b.com", Heading = "headers", DocumentId = docId },
                new DocumentSection { Text = "Body text", Heading = "body", DocumentId = docId },
                new DocumentSection { Text = "attachment content", Heading = "attachment:report.txt", DocumentId = docId }),
            new ChunkingOptions(), TestContext.Current.CancellationToken))
        {
            chunks.Add(c);
        }

        return chunks;
    }

    private static async IAsyncEnumerable<DocumentSection> Sections(params DocumentSection[] sections)
    {
        foreach (var s in sections)
            yield return s;
        await Task.CompletedTask;
    }
}
