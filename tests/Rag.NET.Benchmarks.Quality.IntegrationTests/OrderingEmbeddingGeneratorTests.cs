using Microsoft.Extensions.AI;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The guard on <see cref="OrderingEmbeddingGenerator"/>'s contract, kept separate from the parity
/// tests so it fails on its own terms.
/// <para>
/// A degenerate fixture embedder is not a hypothetical here. Phase 6.2.3's mock constructed
/// <c>new Random(123)</c> inside its callback, so every vector came back byte-identical; identical
/// points collapse to one cluster, no test ever built a RAPTOR tree deeper than one level, and
/// #332, #333 and an unbounded-spend infinite loop all stayed unreachable while the suite was
/// green. A degenerate embedder would break the parity test the same way and worse — ties make
/// reordering invisible, so the assertion would pass by construction.
/// </para>
/// </summary>
public sealed class OrderingEmbeddingGeneratorTests
{
    private static readonly string[] Corpus =
        ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot"];

    [Fact]
    public async Task GenerateAsync_IsDeterministic()
    {
        var generator = new OrderingEmbeddingGenerator(Corpus);
        var ct = TestContext.Current.CancellationToken;

        var first = await generator.GenerateAsync(Corpus, cancellationToken: ct);
        var second = await generator.GenerateAsync(Corpus, cancellationToken: ct);

        for (var i = 0; i < Corpus.Length; i++)
        {
            Assert.Equal(first[i].Vector.ToArray(), second[i].Vector.ToArray());
        }
    }

    [Fact]
    public async Task GenerateAsync_IsInjective()
    {
        var generator = new OrderingEmbeddingGenerator(Corpus);
        var ct = TestContext.Current.CancellationToken;

        var vectors = await generator.GenerateAsync(Corpus, cancellationToken: ct);

        for (var i = 0; i < Corpus.Length; i++)
        {
            for (var j = i + 1; j < Corpus.Length; j++)
            {
                Assert.NotEqual(vectors[i].Vector.ToArray(), vectors[j].Vector.ToArray());
            }
        }
    }

    /// <summary>
    /// The property the parity test depends on: cosine against the query is strictly decreasing in
    /// corpus position, so the top-k has exactly one correct order and any reordering or truncation
    /// is observable. Pairwise-distinct is not enough — two documents tying at the same score would
    /// make a swap between them invisible.
    /// </summary>
    [Fact]
    public async Task Similarities_AreStrictlyDecreasing_AndPairwiseDistinct()
    {
        var generator = new OrderingEmbeddingGenerator(Corpus);
        var ct = TestContext.Current.CancellationToken;

        var query = await generator.GenerateAsync(
            [OrderingEmbeddingGenerator.QueryText], cancellationToken: ct);
        var documents = await generator.GenerateAsync(Corpus, cancellationToken: ct);

        var scores = new double[Corpus.Length];
        for (var i = 0; i < Corpus.Length; i++)
        {
            scores[i] = Dot(query[0].Vector.Span, documents[i].Vector.Span);
        }

        for (var i = 1; i < scores.Length; i++)
        {
            Assert.True(
                scores[i] < scores[i - 1],
                $"score[{i}]={scores[i]} is not strictly below score[{i - 1}]={scores[i - 1]}; " +
                "the fixture no longer imposes a unique ordering and the parity assertion would " +
                "pass by construction.");
        }

        Assert.Equal(scores.Length, scores.Distinct().Count());
    }

    [Fact]
    public async Task GenerateAsync_ThrowsForAnUnknownText()
    {
        var generator = new OrderingEmbeddingGenerator(Corpus);
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(
            () => generator.GenerateAsync(["not in the corpus"], cancellationToken: ct));
    }

    private static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }
}
