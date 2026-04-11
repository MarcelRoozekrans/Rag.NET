using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Persists per-provider file identity records (ETag + SHA-256 hash) to enable
/// incremental ingestion — files unchanged since the last run are skipped.
/// </summary>
public interface IContentHashStore
{
    /// <summary>Returns the stored ETag for the entry, or <see langword="null"/> if unknown.</summary>
    Task<string?> GetETagAsync(ProviderId providerId, EntryId entryId, CancellationToken cancellationToken = default);

    /// <summary>Returns the stored SHA-256 content hash for the entry, or <see langword="null"/> if unknown.</summary>
    Task<string?> GetHashAsync(ProviderId providerId, EntryId entryId, CancellationToken cancellationToken = default);

    /// <summary>Upserts the ETag and hash for an entry. Pass <see langword="null"/> for <paramref name="etag"/> when the provider does not supply one; this overwrites and clears any previously stored ETag for the entry.</summary>
    Task SetAsync(ProviderId providerId, EntryId entryId, string? etag, string hash, CancellationToken cancellationToken = default);

    /// <summary>Returns all entry IDs known for the given provider (used by <see cref="Rag.NET.DataProviders.CleanupMode.Full"/>).</summary>
    Task<IReadOnlySet<EntryId>> GetAllIdsAsync(ProviderId providerId, CancellationToken cancellationToken = default);

    /// <summary>Removes a single entry record.</summary>
    Task RemoveAsync(ProviderId providerId, EntryId entryId, CancellationToken cancellationToken = default);
}
