using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Benchmarks.Quality;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Holds a real <c>AddRagNet</c> pipeline to the harness's dense row over the same store.
/// <para>
/// <b>"Parity" here does not mean what it means elsewhere in this project.</b> A
/// <see cref="BeirParityTests"/> "parity leg" reproduces a published BEIR figure. This type
/// compares the shipped retrieval pipeline against the harness that produces those figures — a
/// different claim about a different pair of things.
/// </para>
/// <para>
/// Every pinned figure in this project comes from <see cref="BeirHarness"/>, which calls
/// <c>store.SearchAsync</c> directly. A user goes through <c>AddRagNet</c>, whose default retrieval
/// chain is seventeen behaviours deep — sixteen of them before the one the harness calls. All
/// sixteen are supposed to no-op at shipped defaults, and until this type existed nothing asserted
/// it: a behaviour that quietly stopped no-opping would change what every user gets while the
/// figures went on describing the old path, with the suite green.
/// </para>
/// </summary>
internal static class PipelineParity
{
    /// <summary>
    /// Runs one query through a default <c>AddRagNet</c> pipeline over the supplied store.
    /// </summary>
    /// <param name="store">
    /// The populated store, handed to the container as an instance. Sharing it by identity is what
    /// leaves the sixteen behaviours as the only variable — rebuilding an equivalent store would
    /// reintroduce indexing as a second one and make a failure unattributable.
    /// </param>
    /// <param name="embedder">The same embedder the store was indexed through.</param>
    /// <param name="query">The query text.</param>
    /// <param name="topK">Set explicitly: <see cref="RetrievalOptions.TopK"/> defaults to 5 while
    /// the harness computes its own cutoff, so leaving each side on its default would fail for a
    /// reason that is not drift.</param>
    /// <param name="ct">Cancels the retrieval.</param>
    /// <returns>The pipeline's hits, projected to the harness's <see cref="ChunkHit"/> shape.</returns>
    /// <remarks>
    /// A fresh container per call, deliberately. <c>ResultCacheBehavior</c> and
    /// <c>EmbeddingCacheBehavior</c> are both in the default chain; if either is not a no-op, a
    /// re-run could agree where a first run did not. This keeps the test on the first-call path,
    /// which is what a user gets.
    /// </remarks>
    public static async Task<IReadOnlyList<ChunkHit>> RetrieveThroughPipelineAsync(
        IVectorStore store,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        string query,
        int topK,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(embedder);

        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(embedder);
        services.AddRagNet();

        using var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<IRagPipeline>();

        var result = await pipeline.RetrieveAsync(
            query, new RetrievalOptions { TopK = topK }, ct);

        if (!result.IsSuccess)
        {
            Assert.Fail(
                $"The pipeline failed to retrieve for '{query}': {result.Error}. This is a failure " +
                "to run, not a parity mismatch — the two are different findings and must not be " +
                "reported as one.");
        }

        return ToChunkHits(result.Value);
    }

    /// <summary>
    /// Asserts the two rankings are identical: same ids, same scores, same order.
    /// </summary>
    /// <param name="harness">The harness's ranking, from <c>AblationRow.Dense</c>.</param>
    /// <param name="pipeline">The pipeline's ranking.</param>
    /// <param name="query">The query, for the message.</param>
    /// <remarks>
    /// Scores are compared exactly. Both sides call the same <c>SearchAsync</c> on the same store,
    /// so identical inputs give bit-identical floats; there is no legitimate source of a small
    /// difference, so a tolerance could only hide an illegitimate one — in particular a query
    /// vector that differs because the pipeline's embedder and the harness's disagree.
    /// </remarks>
    public static void AssertSame(
        IReadOnlyList<ChunkHit> harness,
        IReadOnlyList<ChunkHit> pipeline,
        string query)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(pipeline);

        var shared = Math.Min(harness.Count, pipeline.Count);
        for (var rank = 0; rank < shared; rank++)
        {
            if (string.Equals(harness[rank].ChunkId, pipeline[rank].ChunkId, StringComparison.Ordinal) &&
                harness[rank].Score.Equals(pipeline[rank].Score))
            {
                continue;
            }

            Assert.Fail(Explain(rank, harness[rank], pipeline[rank], query));
        }

        Assert.True(
            harness.Count == pipeline.Count,
            $"'{query}' returned {pipeline.Count} hits through the pipeline and {harness.Count} " +
            $"through the harness, agreeing on the first {shared}. {WhatItMeans}");
    }

    private const string WhatItMeans =
        "Either a default retrieval behaviour stopped being a no-op, or the harness's dense path " +
        "changed. If the behaviour change was deliberate, every pinned figure now describes " +
        "something the shipped pipeline no longer does, and the figures — not this test — are what " +
        "need attention.";

    private static string Explain(int rank, ChunkHit harness, ChunkHit pipeline, string query)
    {
        // Equal scores with different ids is a tie-break divergence, not a vector divergence, and
        // it points somewhere else entirely — so it is worth saying rather than leaving the reader
        // to compare two long numbers by eye.
        var sameScore = harness.Score.Equals(pipeline.Score)
            ? " The scores are equal, so this is a tie-break difference rather than a different " +
              "query vector."
            : string.Empty;

        return
            $"Rank {rank} differs for '{query}' — pipeline {pipeline.ChunkId} ({pipeline.Score}) " +
            $"vs harness {harness.ChunkId} ({harness.Score}).{sameScore} {WhatItMeans}";
    }

    /// <summary>
    /// Projects to the harness's hit shape. The id format is copied from
    /// <c>AblationRow.ToChunkHits</c>, which is <c>private protected</c> and cannot be called; this
    /// is the one duplication the design accepts, because the alternative is widening the harness.
    /// </summary>
    /// <remarks>
    /// <see langword="internal"/> rather than <see langword="private"/>: this is the one in-assembly
    /// copy of the projection the plan's global constraints accept, and it is shared with the
    /// parity tests so a second, near-identical copy is never written alongside it.
    /// </remarks>
    internal static IReadOnlyList<ChunkHit> ToChunkHits(IReadOnlyList<SearchResult> results)
    {
        var hits = new ChunkHit[results.Count];
        for (var i = 0; i < results.Count; i++)
        {
            var chunk = results[i].Chunk;
            hits[i] = new ChunkHit(
                FormattableString.Invariant($"{chunk.DocumentId.Value}#{chunk.ChunkIndex}"),
                chunk.DocumentId.Value,
                results[i].Score);
        }

        return hits;
    }
}
