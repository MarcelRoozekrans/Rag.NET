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
/// <c>chunk.Metadata["updated_at"]</c>, falling back to <c>chunk.Metadata["created_at"]</c> and
/// then <see cref="TimeWeightedOptions.FallbackMetadataKeys"/> (all written at ingest time by
/// <see cref="Rag.NET.Ingestion.Behaviors.MetadataBehavior"/> from
/// <see cref="Rag.NET.Models.DocumentMetadata.UpdatedAt"/>/<see cref="Rag.NET.Models.DocumentMetadata.CreatedAt"/>).
/// Results are re-sorted by the combined score before being returned. See
/// <see cref="ResolveTimestamp"/> for the exact precedence.
/// </summary>
public sealed class TimeWeightedRetriever : IRetriever
{
    /// <summary>Alias of the canonical constant — see <see cref="ReservedMetadataKeys.CreatedAt"/>.</summary>
    internal const string CreatedAtKey = ReservedMetadataKeys.CreatedAt;

    /// <summary>Alias of the canonical constant — see <see cref="ReservedMetadataKeys.UpdatedAt"/>.</summary>
    internal const string UpdatedAtKey = ReservedMetadataKeys.UpdatedAt;

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

        var ageHours = Math.Max(0.0, (now - timestamp.Value).TotalHours);
        return Math.Exp(-_options.DecayRate * ageHours);
    }

    /// <summary>
    /// Resolution order: <see cref="UpdatedAtKey"/> → <see cref="CreatedAtKey"/> →
    /// <see cref="TimeWeightedOptions.FallbackMetadataKeys"/> → absent (neutral). Freshness is a
    /// last-changed question, so a dedicated modified timestamp outranks a creation timestamp,
    /// which in turn outranks any connector-specific fallback key.
    /// </summary>
    private DateTime? ResolveTimestamp(TextChunk chunk)
    {
        if (TryResolveKey(chunk, UpdatedAtKey, out var updatedAt))
            return updatedAt;

        if (TryResolveKey(chunk, CreatedAtKey, out var createdAt))
            return createdAt;

        foreach (var key in _options.FallbackMetadataKeys)
        {
            if (TryResolveKey(chunk, key, out var fallback))
                return fallback;
        }

        return null;
    }

    /// <summary>
    /// Looks up <paramref name="key"/> on <paramref name="chunk"/> and parses it. Returns
    /// <see langword="false"/> both when the key is absent and when its value fails to parse —
    /// either way, resolution must fall through to the next key in the order, not stop here.
    /// </summary>
    private bool TryResolveKey(TextChunk chunk, string key, out DateTime timestamp)
    {
        timestamp = default;
        if (!chunk.Metadata.TryGetValue(key, out var raw))
            return false;

        // The framework writes these keys as ISO-8601 strings (see MetadataBehavior), but a
        // caller-supplied value may already be a typed date — honour it directly.
        if (raw.Kind == MetadataValueKind.DateTimeOffset)
        {
            timestamp = raw.DateTimeOffsetValue.UtcDateTime;
            return true;
        }

        if (raw.Kind == MetadataValueKind.String
            && DateTime.TryParse(raw.StringValue, null, DateTimeStyles.RoundtripKind, out timestamp))
        {
            return true;
        }

        _logger?.LogWarning("Chunk {DocumentId}[{ChunkIndex}] has unparseable \"{Key}\" value \"{Value}\"; skipping",
            chunk.DocumentId, chunk.ChunkIndex, key, raw);
        return false;
    }
}
