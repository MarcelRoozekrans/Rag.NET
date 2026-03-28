using System.Globalization;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Rag.NET.Retrieval;

/// <summary>
/// <see cref="IRetriever"/> decorator that re-scores search results by multiplying each
/// similarity score by <c>e^(−λ × age_hours)</c> where age is derived from
/// <c>chunk.Metadata["created_at"]</c> (written at ingest time by
/// <see cref="Rag.NET.Ingestion.Behaviors.MetadataBehavior"/> from
/// <see cref="Rag.NET.Models.DocumentMetadata.CreatedAt"/>).
/// Results are re-sorted by the combined score before being returned.
/// </summary>
public sealed class TimeWeightedRetriever : IRetriever
{
    internal const string CreatedAtKey = "created_at";

    private readonly IRetriever _inner;
    private readonly TimeWeightedOptions _options;
    private readonly ILogger<TimeWeightedRetriever>? _logger;

    public TimeWeightedRetriever(
        IRetriever inner,
        TimeWeightedOptions options,
        ILogger<TimeWeightedRetriever>? logger = null)
    {
        _inner   = inner;
        _options = options;
        _logger  = logger;
    }

    public async Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effective = options ?? new RetrievalOptions();

        if (!effective.UseTimeWeighting)
            return await _inner.RetrieveAsync(query, effective, cancellationToken).ConfigureAwait(false);

        var result = await _inner.RetrieveAsync(query, effective, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            return result;

        var now = DateTime.UtcNow;
        List<SearchResult> rescored = result.Value
            .Select(r => r with { Score = r.Score * ComputeDecay(r.Chunk, now) })
            .OrderByDescending(r => r.Score)
            .ToList();

        return Result<IReadOnlyList<SearchResult>, RagError>.Success(rescored);
    }

    private double ComputeDecay(TextChunk chunk, DateTime now)
    {
        var timestamp = ResolveTimestamp(chunk);
        if (timestamp is null)
            return 1.0;

        var ageHours = (now - timestamp.Value).TotalHours;
        return Math.Exp(-_options.DecayRate * ageHours);
    }

    private DateTime? ResolveTimestamp(TextChunk chunk)
    {
        if (chunk.Metadata.TryGetValue(CreatedAtKey, out var raw) &&
            DateTime.TryParse(raw, null, DateTimeStyles.RoundtripKind, out var dt))
            return dt;

        foreach (var key in _options.FallbackMetadataKeys)
        {
            if (chunk.Metadata.TryGetValue(key, out var fallback) &&
                DateTime.TryParse(fallback, null, DateTimeStyles.RoundtripKind, out var fdt))
                return fdt;
        }

        return null;
    }
}
