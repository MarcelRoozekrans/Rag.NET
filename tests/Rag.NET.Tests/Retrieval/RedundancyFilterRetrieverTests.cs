using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

public class RedundancyFilterRetrieverTests
{
    private readonly IRetriever _inner = Substitute.For<IRetriever>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly RedundancyFilterRetriever _sut;

    public RedundancyFilterRetrieverTests()
    {
        _sut = new RedundancyFilterRetriever(_inner, _embedder);
    }

    private static SearchResult MakeResult(string docId, int chunkIndex, double score, string text = "text") =>
        new()
        {
            Chunk = new TextChunk { Text = text, DocumentId = docId, ChunkIndex = chunkIndex },
            Score = score
        };

    [Fact]
    public async Task RetrieveAsync_UseRedundancyFilterTrue_FiltersDuplicateResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9, "hello"),
            MakeResult("doc-2", 0, 0.8, "hello again"),
        };

        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(results);

        // Return identical vectors so cosine similarity = 1.0, above default threshold 0.95
        var identicalVector = new float[] { 1.0f, 0.0f, 0.0f };
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), ct)
            .Returns(new GeneratedEmbeddings<Embedding<float>>(
            [
                new Embedding<float>(identicalVector),
                new Embedding<float>(identicalVector),
            ]));

        var opts = new RetrievalOptions { UseRedundancyFilter = true };
        var filtered = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Single(filtered);
        Assert.Equal("doc-1", filtered[0].Chunk.DocumentId);
    }

    [Fact]
    public async Task RetrieveAsync_UseRedundancyFilterFalse_PassesThroughWithoutCallingEmbedder()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
            MakeResult("doc-2", 0, 0.8),
        };

        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(results);

        var opts = new RetrievalOptions { UseRedundancyFilter = false };
        var output = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Equal(2, output.Count);
        await _embedder.DidNotReceiveWithAnyArgs()
            .GenerateAsync(default!, default, ct);
    }
}
