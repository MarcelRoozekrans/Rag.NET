using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Produces learned sparse embeddings (e.g. SPLADE) from text: a <see cref="SparseVector"/>
/// of vocabulary term ids with positive weights, suitable for inverted-index style retrieval.
/// </summary>
public interface ISparseEmbeddingGenerator
{
    /// <summary>
    /// Encodes <paramref name="text"/> into a sparse vector. Empty or whitespace-only input
    /// yields an empty vector (<see cref="SparseVector.Count"/> == 0) rather than throwing.
    /// </summary>
    ValueTask<SparseVector> GenerateAsync(string text, CancellationToken cancellationToken = default);
}
