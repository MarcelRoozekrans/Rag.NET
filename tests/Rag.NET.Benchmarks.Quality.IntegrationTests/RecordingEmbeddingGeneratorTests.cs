using Microsoft.Extensions.AI;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The recorder the Semantic Kernel entrant hands to
/// <c>VectorStoreCollectionOptions.EmbeddingGenerator</c>, exercised offline against a fixture
/// source — no ONNX model.
/// <para>
/// The recorder is the entrant's evidence, so what these tests pin is its honesty: every text the
/// library asks to embed is recorded <b>verbatim</b>, every vector handed back is the source's
/// <b>unmodified</b>, and nothing is reordered. A recorder that normalised, copied wrongly or
/// dropped a request would let the row silently measure a different embedder — the exact table
/// this comparison's design rejected.
/// </para>
/// </summary>
public sealed class RecordingEmbeddingGeneratorTests
{
    [Fact]
    public async Task GenerateAsync_ReturnsTheSourcesVectorsVerbatim_InRequestOrder()
    {
        float[] first = [0.1f, 0.2f];
        float[] second = [0.3f, 0.4f];
        using var recorder = new RecordingEmbeddingGenerator(
            (texts, _) => Task.FromResult<IReadOnlyList<float[]>>([first, second]));

        var embeddings = await recorder.GenerateAsync(
            ["alpha", "beta"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, embeddings.Count);
        Assert.Equal(first, embeddings[0].Vector.ToArray());
        Assert.Equal(second, embeddings[1].Vector.ToArray());
    }

    [Fact]
    public async Task GenerateAsync_RecordsEveryRequestedText_AcrossCalls_InOrder()
    {
        using var recorder = new RecordingEmbeddingGenerator((texts, _) => Vectors(texts));

        _ = await recorder.GenerateAsync(
            ["doc one", "doc two"], cancellationToken: TestContext.Current.CancellationToken);
        _ = await recorder.GenerateAsync(
            ["a query"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["doc one", "doc two", "a query"], recorder.RequestedTexts);
    }

    [Fact]
    public async Task GenerateAsync_HandsTheSourceExactlyTheTextsItWasAsked()
    {
        IReadOnlyList<string>? seen = null;
        using var recorder = new RecordingEmbeddingGenerator(
            (texts, _) =>
            {
                seen = texts;
                return Vectors(texts);
            });

        _ = await recorder.GenerateAsync(
            ["gamma"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(seen);
        Assert.Equal(["gamma"], seen);
    }

    [Fact]
    public async Task GenerateAsync_RecordsTheVectorsItHandedBack_BesideTheirTexts()
    {
        // The produced vectors are the identity check's evidence: what Semantic Kernel received
        // for a known string is compared against the pinned generator called directly, and that
        // comparison needs the vector as it crossed this boundary, not a re-derivation.
        using var recorder = new RecordingEmbeddingGenerator((texts, _) => Vectors(texts));

        _ = await recorder.GenerateAsync(
            ["ab", "c"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, recorder.ProducedVectors.Count);
        Assert.Equal([2f], recorder.ProducedVectors[0]);
        Assert.Equal([1f], recorder.ProducedVectors[1]);
    }

    private static Task<IReadOnlyList<float[]>> Vectors(IReadOnlyList<string> texts)
    {
        var vectors = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
        {
            vectors[i] = [texts[i].Length];
        }

        return Task.FromResult<IReadOnlyList<float[]>>(vectors);
    }
}
