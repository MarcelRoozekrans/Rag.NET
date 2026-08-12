using Microsoft.Extensions.AI;

namespace Rag.NET.Benchmarks.Quality.GraphExtractions;

/// <summary>
/// The embedder the generation run hands <see cref="GraphRagSliceIngestion"/>: it returns
/// fixed-length zero vectors and embeds nothing.
/// <para>
/// <b>The generation run exists to fill the extraction cache and does nothing else with what it
/// extracts.</b> Its graph store is thrown away per document, its embedded chunks are discarded,
/// and nothing it computes is ever searched — the guard rebuilds all of that from the cache, with
/// the real ONNX embedder. Embedding tens of thousands of entity descriptions here would add
/// minutes per run and produce vectors nobody reads.
/// </para>
/// <para>
/// <b>It is safe precisely because it cannot reach the cache.</b> The cache key is the rendered
/// prompt; nothing embedded here enters a prompt, and the extraction calls happen before any
/// embedding does. A stub in the guard would be a defect — nothing would be retrievable — which is
/// why this type lives beside the tool and the guard constructs its own real generator.
/// </para>
/// </summary>
public sealed class StubEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>The dimension of the zero vectors returned, matching all-MiniLM-L6-v2's 384.</summary>
    private const int Dimensions = 384;

    /// <inheritdoc/>
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var embeddings = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var _ in values)
        {
            embeddings.Add(new Embedding<float>(new float[Dimensions]));
        }

        return Task.FromResult(embeddings);
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Nothing to release: this generator holds no model, no session and no native handle.
    }
}
