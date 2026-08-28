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
    /// <summary>
    /// The parity leg's own corpus, deliberately rather than a look-alike list declared here.
    /// A separately declared corpus guards <see cref="PipelineParityTests.Corpus"/> only for as
    /// long as the two happen to agree; sharing it means growing the parity corpus re-guards it.
    /// </summary>
    private static readonly IReadOnlyList<string> Corpus = PipelineParityTests.Corpus;

    [Fact]
    public async Task GenerateAsync_IsDeterministic()
    {
        var generator = new OrderingEmbeddingGenerator(Corpus);
        var ct = TestContext.Current.CancellationToken;

        var first = await generator.GenerateAsync(Corpus, cancellationToken: ct);
        var second = await generator.GenerateAsync(Corpus, cancellationToken: ct);

        for (var i = 0; i < Corpus.Count; i++)
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

        for (var i = 0; i < Corpus.Count; i++)
        {
            for (var j = i + 1; j < Corpus.Count; j++)
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

        var scores = new double[Corpus.Count];
        for (var i = 0; i < Corpus.Count; i++)
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

    /// <summary>
    /// Non-degeneracy in the sense the multi-query parity leg needs: a query must interrogate the
    /// ranking from a point no document occupies, because a query sitting <i>on</i> a document is
    /// where whole classes of reordering cancel out — MMR provably no-ops on
    /// <see cref="OrderingEmbeddingGenerator.QueryText"/> for exactly that reason.
    /// <para>
    /// <see cref="OrderingEmbeddingGenerator.QueryText"/> is the one deliberate coincidence, kept
    /// because its hand-checked ranking is the parity leg's pin, so it is asserted <i>positively</i>
    /// here rather than skipped: if it ever stops coinciding with document 0 that is a change to the
    /// fixture's geometry, and the pin needs rechecking.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OnlyQueryText_SitsOnADocumentVector()
    {
        var generator = new OrderingEmbeddingGenerator(Corpus);
        var ct = TestContext.Current.CancellationToken;

        var queries = await generator.GenerateAsync(generator.QueryTexts, cancellationToken: ct);
        var documents = await generator.GenerateAsync(Corpus, cancellationToken: ct);

        Assert.Equal(documents[0].Vector.ToArray(), queries[0].Vector.ToArray());
        Assert.Equal(OrderingEmbeddingGenerator.QueryText, generator.QueryTexts[0]);

        Assert.True(
            generator.QueryTexts.Count > 1,
            "the fixture offers only the degenerate query; the parity leg would be back to one " +
            "query, at the one angle where reordering cancels out.");

        for (var q = 1; q < generator.QueryTexts.Count; q++)
        {
            for (var i = 0; i < Corpus.Count; i++)
            {
                Assert.NotEqual(documents[i].Vector.ToArray(), queries[q].Vector.ToArray());
            }
        }
    }

    [Fact]
    public async Task GenerateAsync_ThrowsForAnUnknownText()
    {
        var generator = new OrderingEmbeddingGenerator(Corpus);
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(
            () => generator.GenerateAsync(["not in the corpus"], cancellationToken: ct));
    }

    /// <summary>
    /// A corpus entry colliding with a query text used to be silent: the constructor seeded the
    /// query vectors and then let the corpus loop overwrite them. Loud is the only acceptable
    /// behaviour — the overwritten query would compare against itself and pass.
    /// </summary>
    [Fact]
    public void Constructor_ThrowsWhenTheCorpusContainsAQueryText()
    {
        var probe = new OrderingEmbeddingGenerator(Corpus);

        var collidingCorpus = new string[Corpus.Count];
        for (var i = 0; i < Corpus.Count; i++)
        {
            collidingCorpus[i] = Corpus[i];
        }

        foreach (var queryText in probe.QueryTexts)
        {
            collidingCorpus[0] = queryText;

            var exception = Assert.Throws<ArgumentException>(
                () => new OrderingEmbeddingGenerator(collidingCorpus));

            Assert.Contains(queryText, exception.Message, StringComparison.Ordinal);
        }
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
