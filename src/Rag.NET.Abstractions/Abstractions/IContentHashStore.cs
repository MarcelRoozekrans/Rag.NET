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

    /// <summary>
    /// Records that an entry was examined by an ingestion run and found unchanged.
    /// </summary>
    /// <param name="providerId">The provider the entry belongs to.</param>
    /// <param name="entryId">The entry that was checked.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// <b>"We looked and nothing had changed" had no way to be recorded, which made "is my index
    /// fresh?" unanswerable per document</b> (issue #435). A fully-unchanged entry writes nothing:
    /// an entry whose ETag still matches is skipped before its content is opened or its hash read,
    /// and an entry with no ETag whose content hash matches is skipped without a write either.
    /// <see cref="SetAsync"/> therefore fires only when something actually changed, so a timestamp
    /// stamped there records <i>last changed</i> — which is roughly what the document's own
    /// <c>updated_at</c> already says.
    /// </para>
    /// <para>
    /// <b>It carries no hash or ETag on purpose.</b> The ETag fast path returns before reading
    /// either, and making this method need them would force a store read per unchanged entry per
    /// run purely to write the value straight back.
    /// </para>
    /// <para>
    /// <b>Implementations may no-op.</b> Nothing in the pipeline reads what this records; it exists
    /// so a store CAN observe the check. It is called only on the skip paths — a run that ingests
    /// calls <see cref="SetAsync"/> instead, and a store wanting a single "last seen" should stamp
    /// both.
    /// </para>
    /// </remarks>
    Task TouchAsync(ProviderId providerId, EntryId entryId, CancellationToken cancellationToken = default);
}
