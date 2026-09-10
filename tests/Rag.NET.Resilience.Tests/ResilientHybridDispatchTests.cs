using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Polly;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Resilience;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.Resilience;

/// <summary>
/// The two halves of #544 wired together: the real <see cref="ResilientHybridVectorStore"/> driving
/// the real <see cref="EnsembleBehavior"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Added because the 6.2.37 mutation sweep found neither suite covered the seam.</b> Dropping
/// <c>NativeOnlyCapability</c> forwarding from the decorator was caught by the resilience unit test
/// and <i>not</i> by the retrieval one — because that test's forwarding decorator is a fake which
/// forwards the member itself and never touches <see cref="ResilientHybridVectorStore"/>. So the
/// retrieval suite proved the <i>contract</i> ("a forwarding decorator makes the refusal fire") and
/// the resilience suite proved the <i>delegation</i>, and nothing proved the real decorator
/// satisfies the real contract.
/// </para>
/// <para>
/// That gap is the exact shape of #544 itself — two layers each correct in isolation that did not
/// compose — which makes leaving it untested the least defensible option available.
/// </para>
/// </remarks>
public class ResilientHybridDispatchTests
{
    /// <summary>A hybrid store declaring a native-only capability, as Azure AI Search does with the ranker on.</summary>
    private sealed class RankingHybridStore : IVectorStore, IHybridSearchable
    {
        public string? NativeOnlyCapability => "semantic ranking";

        public Task<IReadOnlyList<SearchResult>> HybridSearchAsync(
            string textQuery, ReadOnlyMemory<float> queryEmbedding, SearchOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchResult>>([Result("native")]);

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding, SearchOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchResult>>([Result("dense")]);

        public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        private static SearchResult Result(string docId) => new()
        {
            Chunk = new TextChunk { Text = docId, DocumentId = new DocumentId(docId), ChunkIndex = 0 },
            Score = 1.0,
        };
    }

    private static EnsembleBehavior BehaviourOver(IVectorStore store)
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 1f, 0f, 0f })]));

        return new EnsembleBehavior
        {
            Embedder = embedder,
            VectorStore = store,
            Bm25Index = Substitute.For<IBm25Index>(),
        };
    }

    private static RetrievalContext Context(RetrievalOptions options) =>
        new() { Query = "test query", Options = options, Logger = NullLogger.Instance };

    /// <summary>
    /// The headline fix: a resilience-decorated hybrid store reaches the native path, where before
    /// #544 the probe failed and every query fell back to client-side fusion.
    /// </summary>
    [Fact]
    public async Task ADecoratedHybridStore_ReachesTheNativePath()
    {
        var ct = TestContext.Current.CancellationToken;
        var decorated = ResilientVectorStore.Create(new RankingHybridStore(), ResiliencePipeline.Empty);
        var sut = BehaviourOver(decorated);

        var output = await sut.HandleAsync(
            Context(new RetrievalOptions { UseHybridSearch = true }), ct,
            (_, _) => throw new InvalidOperationException("must not call next"));

        var only = Assert.Single(output);
        Assert.Equal(new DocumentId("native"), only.Chunk.DocumentId);
    }

    /// <summary>
    /// And the guard on that capability reaches it too. <b>This is the test the mutation sweep
    /// asked for</b>: it fails if <see cref="ResilientHybridVectorStore"/> forwards
    /// <c>HybridSearchAsync</c> but leaves <c>NativeOnlyCapability</c> at its <see langword="null"/>
    /// interface default — the half-done fix that restores the capability while leaving the refusal
    /// unable to fire.
    /// </summary>
    [Fact]
    public async Task ADecoratedHybridStore_StillRefusesWhenTheNativePathIsUnreachable()
    {
        var ct = TestContext.Current.CancellationToken;
        var decorated = ResilientVectorStore.Create(new RankingHybridStore(), ResiliencePipeline.Empty);
        var sut = BehaviourOver(decorated);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.HandleAsync(
                Context(new RetrievalOptions { UseHybridSearch = true, MinScore = 0.7 }), ct,
                (_, _) => ValueTask.FromResult<IReadOnlyList<SearchResult>>([])).AsTask());

        Assert.Contains("semantic ranking", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(RetrievalOptions.MinScore), ex.Message, StringComparison.Ordinal);
    }
}
