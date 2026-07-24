namespace Rag.NET.Models;

/// <summary>
/// A sparse embedding: parallel arrays of vocabulary term ids and their weights, as produced
/// by learned sparse encoders such as SPLADE.
/// </summary>
/// <remarks>
/// Invariants (producers must guarantee, consumers may assume):
/// <list type="bullet">
/// <item><description><see cref="Indices"/> holds strictly ascending, unique term ids.</description></item>
/// <item><description><see cref="Values"/> is parallel to <see cref="Indices"/> (same length,
/// element <c>i</c> is the weight of term <c>Indices[i]</c>).</description></item>
/// <item><description>All weights are &gt; 0 — zero-weight terms are omitted entirely, so an
/// empty vector (<see cref="Count"/> == 0) represents "no terms".</description></item>
/// </list>
/// </remarks>
public sealed record SparseVector
{
    /// <summary>Strictly ascending, unique vocabulary term ids.</summary>
    public required ReadOnlyMemory<int> Indices { get; init; }

    /// <summary>Weights parallel to <see cref="Indices"/>; every weight is &gt; 0.</summary>
    public required ReadOnlyMemory<float> Values { get; init; }

    /// <summary>Number of non-zero terms in this vector.</summary>
    public int Count => Indices.Length;

    /// <summary>A sparse vector with no terms (e.g. the encoding of empty text).</summary>
    public static SparseVector Empty { get; } = new()
    {
        Indices = ReadOnlyMemory<int>.Empty,
        Values = ReadOnlyMemory<float>.Empty,
    };
}
