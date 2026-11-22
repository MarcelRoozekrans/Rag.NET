using System.Text.Json.Serialization;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Api.Contracts;

/// <summary><c>RetrievalOptions</c> on the wire. A field left out takes the in-process default.</summary>
/// <remarks>
/// <c>RetrievalOptions.Filter</c> is a specification object, code rather than data, so it has no
/// field here; the clients refuse to send options that set it.
/// </remarks>
public sealed record RetrievalOptionsDto
{
    /// <summary>Mirrors <c>RetrievalOptions.TopK</c>.</summary>
    public int? TopK { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.MinScore</c>.</summary>
    public double? MinScore { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.MetadataFilter</c>. Values keep their kind on the wire.</summary>
    public IDictionary<string, MetadataValue>? MetadataFilter { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.UseHybridSearch</c>. Left out means <see langword="false"/>.</summary>
    public bool? UseHybridSearch { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.EnsembleOptions</c>.</summary>
    public EnsembleOptionsDto? EnsembleOptions { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.UseSparseSearch</c>.</summary>
    public bool? UseSparseSearch { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.UseLostInTheMiddleReordering</c>.</summary>
    public bool? UseLostInTheMiddleReordering { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.UseRedundancyFilter</c>.</summary>
    public bool? UseRedundancyFilter { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.RedundancyThreshold</c>.</summary>
    public float? RedundancyThreshold { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.MaxContextTokens</c>.</summary>
    public int? MaxContextTokens { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.UseMmr</c>.</summary>
    public bool? UseMmr { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.MmrLambda</c>.</summary>
    public float? MmrLambda { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.MmrCandidateCount</c>.</summary>
    public int? MmrCandidateCount { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.UseMultiQuery</c>.</summary>
    public bool? UseMultiQuery { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.UseReranking</c>.</summary>
    public bool? UseReranking { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.CandidateCount</c>.</summary>
    public int? CandidateCount { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.UseHyde</c>.</summary>
    public bool? UseHyde { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.UseAdaptiveRetrieval</c>.</summary>
    public bool? UseAdaptiveRetrieval { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.UseCrag</c>.</summary>
    public bool? UseCrag { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.CragScoreThreshold</c>.</summary>
    public float? CragScoreThreshold { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.CragFallbackMode</c>, by name.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<CragFallbackMode>))]
    public CragFallbackMode? CragFallbackMode { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.UseSelfQuery</c>.</summary>
    public bool? UseSelfQuery { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.UseCacheEmbedding</c>.</summary>
    public bool? UseCacheEmbedding { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.UseCacheResult</c>.</summary>
    public bool? UseCacheResult { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.UseParentDocument</c>.</summary>
    public bool? UseParentDocument { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.UseTagRetrieval</c>.</summary>
    public bool? UseTagRetrieval { get; init; }

    /// <summary>Mirrors <c>RetrievalOptions.UseTimeWeighting</c>.</summary>
    public bool? UseTimeWeighting { get; init; }
}
