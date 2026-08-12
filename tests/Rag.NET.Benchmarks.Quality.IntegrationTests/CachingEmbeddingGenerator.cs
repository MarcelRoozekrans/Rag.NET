using Microsoft.Extensions.AI;
using Rag.NET.Benchmarks.Quality;
using Rag.NET.Embeddings.Onnx;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// <see cref="OnnxEmbeddingGenerator"/> behind <see cref="EmbeddingCache"/>, in the shape
/// GraphRAG's behaviors take an embedder in.
/// <para>
/// The GraphRAG guard embeds far more texts than any other case here: one per entity description,
/// one per relationship description and one per community report, over a slice that produces tens
/// of thousands of them. Every other measurement in this project embeds through the cache, for the
/// same reason — a re-run pays only for texts it has not seen — and the behaviors take an
/// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> rather than the harness's own
/// <c>EmbedAsync</c>, so the cache has to reach them through an adapter.
/// </para>
/// </summary>
/// <remarks>
/// The vectors are cached, stored and returned <b>verbatim</b>.
/// <see cref="OnnxEmbeddingGenerator"/> already mean-pools excluding padding and L2-normalises, so
/// normalising again here would not throw — it would quietly move every score.
/// </remarks>
internal sealed class CachingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly OnnxEmbeddingGenerator _generator;
    private readonly EmbeddingCache _cache;

    /// <summary>Creates the adapter.</summary>
    /// <param name="generator">The embedder; the caller disposes it.</param>
    /// <param name="cache">The vector cache every text goes through.</param>
    public CachingEmbeddingGenerator(OnnxEmbeddingGenerator generator, EmbeddingCache cache)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(cache);

        _generator = generator;
        _cache = cache;
    }

    /// <inheritdoc/>
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var texts = new List<string>(values);
        var vectors = await BeirHarness.EmbedAsync(_generator, _cache, texts, cancellationToken);

        var embeddings = new GeneratedEmbeddings<Embedding<float>>();
        for (var i = 0; i < vectors.Count; i++)
        {
            embeddings.Add(new Embedding<float>(vectors[i]));
        }

        return embeddings;
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceType.IsInstanceOfType(this) ? this : _generator.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // The generator is owned by the caller — it is also the harness's own embedder for this
        // run — so disposing it here would close a session somebody else is still using.
    }
}
