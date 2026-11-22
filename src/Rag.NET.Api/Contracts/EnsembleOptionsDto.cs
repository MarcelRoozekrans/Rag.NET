namespace Rag.NET.Api.Contracts;

/// <summary><c>EnsembleOptions</c> on the wire. A field left out takes the in-process default.</summary>
public sealed record EnsembleOptionsDto
{
    /// <summary>Mirrors <c>EnsembleOptions.DenseWeight</c>.</summary>
    public float? DenseWeight { get; init; }

    /// <summary>Mirrors <c>EnsembleOptions.Bm25Weight</c>.</summary>
    public float? Bm25Weight { get; init; }

    /// <summary>Mirrors <c>EnsembleOptions.SparseWeight</c>.</summary>
    public float? SparseWeight { get; init; }

    /// <summary>Mirrors <c>EnsembleOptions.K</c>.</summary>
    public int? K { get; init; }
}
