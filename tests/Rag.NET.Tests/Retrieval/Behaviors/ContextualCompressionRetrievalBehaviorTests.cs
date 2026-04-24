using NSubstitute;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.QueryTechniques.ContextualCompression;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Xunit;

namespace Rag.NET.Tests.Retrieval.Behaviors;

public class ContextualCompressionRetrievalBehaviorTests
{
    [Fact]
    public async Task HandleAsync_InvokesCompressorOnPipelineResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipelineResults = new List<SearchResult>
        {
            new() { Chunk = new TextChunk { Text = "t", DocumentId = new DocumentId("d"), ChunkIndex = 0 }, Score = 0.5 },
        };
        var compressed = new List<SearchResult>
        {
            pipelineResults[0] with { CompressedText = "compressed" },
        };
        var compressor = Substitute.For<IContextualCompressor>();
#pragma warning disable EPS06 // ValueTask struct copy — intentional test double setup via NSubstitute
        compressor.CompressAsync(Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<IReadOnlyList<SearchResult>>(compressed));
#pragma warning restore EPS06

        var sut = new ContextualCompressionRetrievalBehavior(compressor);
        var ctx = new RetrievalContext { Query = "q", Options = new RetrievalOptions() };

        var result = await sut.HandleAsync(ctx, ct,
            (_, _) => ValueTask.FromResult<IReadOnlyList<SearchResult>>(pipelineResults));

        Assert.Equal("compressed", result[0].CompressedText);
        await compressor.Received(1).CompressAsync(pipelineResults, "q", Arg.Any<CancellationToken>());
    }
}
