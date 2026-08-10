using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders;

/// <summary>Extension methods for batch ingestion via <see cref="IFileContentProvider"/>.</summary>
public static class RagPipelineExtensions
{
    private enum EntryOutcome { Ingested, Skipped }

    /// <summary>
    /// Ingests all files from <paramref name="provider"/>, skipping unchanged files when
    /// <paramref name="hashStore"/> is supplied. Optionally deletes disappeared documents
    /// when <paramref name="cleanupMode"/> is <see cref="CleanupMode.Full"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every ingested document is tagged with <see cref="ReservedMetadataKeys.ProviderId"/></b>
    /// carrying <paramref name="providerId"/>, so documents can be filtered or re-ingested by
    /// source without per-connector work. It is written last and therefore overrides any
    /// <c>provider_id</c> in <paramref name="baseMetadata"/>'s tags.
    /// </para>
    /// <para>
    /// <b>Precedence:</b> <paramref name="baseMetadata"/> tags first, then the entry's own
    /// metadata (entry wins on collision), then <c>provider_id</c> (wins over both).
    /// </para>
    /// <para>
    /// <b>Why only entry metadata is reserved-key guarded.</b> Entry metadata comes from
    /// connector code, where a reserved key is always a bug. <paramref name="baseMetadata"/>
    /// comes from the caller and is deliberately left unguarded, because it is the sanctioned —
    /// and only — channel for setting <see cref="ReservedMetadataKeys.AllowedRoles"/> and
    /// <see cref="ReservedMetadataKeys.TrustLevel"/>: those two keys are reserved but written by
    /// nobody in the framework, which only ever reads them (in the RBAC and trust-level
    /// retrieval guards). Guarding base metadata would break RBAC and trust-level tagging
    /// outright. The asymmetry is intentional, not an oversight.
    /// </para>
    /// </remarks>
    /// <exception cref="ReservedMetadataKeyException">
    /// A connector emitted an entry-metadata key the framework reserves for itself. Unlike every
    /// other failure here — which is collected into <see cref="ProviderIngestionResult.Errors"/>
    /// — this <b>escapes the method</b>: it is a deterministic authoring bug that would otherwise
    /// repeat once per document while shipping a corrupted ranking.
    /// <para>
    /// It arrives <b>unwrapped</b>, including under parallel ingestion: although
    /// <see cref="Parallel.ForEachAsync{TSource}(IEnumerable{TSource}, ParallelOptions, Func{TSource, CancellationToken, ValueTask})"/>
    /// faults its task through an <see cref="AggregateException"/>, awaiting unwraps it — so
    /// <c>catch (ReservedMetadataKeyException)</c> is safe and needs no
    /// <see cref="AggregateException"/> handling.
    /// </para>
    /// <para>
    /// <b>Ingestion is left partially complete.</b> When a connector emits the reserved key on
    /// only some entries, the clean ones already processed are ingested and — with a
    /// <paramref name="hashStore"/> — hash-recorded; the throw unwinds none of that. Because the
    /// method throws rather than returns, the accumulated error bag is discarded and cleanup for
    /// <see cref="CleanupMode.Full"/> is skipped, so no document is deleted.
    /// </para>
    /// <para>
    /// <b>Re-running after the fix is safe.</b> Whatever was ingested was collision-free by
    /// definition, and the hash store makes the re-run skip it as unchanged.
    /// </para>
    /// </exception>
    public static async Task<ProviderIngestionResult> IngestFromProviderAsync(
        this IRagPipeline pipeline,
        IFileContentProvider provider,
        ProviderId providerId,
        IContentHashStore? hashStore = null,
        DocumentMetadata? baseMetadata = null,
        IngestionOptions? options = null,
        CleanupMode cleanupMode = CleanupMode.None,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var optionsError = ValidateOptions(options);
        if (optionsError is not null)
            return new ProviderIngestionResult(0, 0, 0, [optionsError]);

        var ingested = 0;
        var skipped = 0;
        var deleted = 0;
        var errors = new ConcurrentBag<RagError>();

        IReadOnlySet<EntryId> knownIds = hashStore is not null && cleanupMode == CleanupMode.Full
            ? await hashStore.GetAllIdsAsync(providerId, cancellationToken).ConfigureAwait(false)
            : (IReadOnlySet<EntryId>)new HashSet<EntryId>();

        var seenIds = new ConcurrentDictionary<EntryId, byte>();

        // Collect entries first — IAsyncEnumerable cannot be iterated in parallel directly
        var entries = new List<FileEntry>();
        await foreach (var result in provider.GetFilesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (result.IsFailure) { errors.Add(result.Error); continue; }
            entries.Add(result.Value);
        }

        var maxParallelism = options?.MaxDegreeOfParallelism ?? 1;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxParallelism,
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(entries, parallelOptions, async (entry, _) =>
        {
            seenIds.TryAdd(entry.Id, 0);
            var outcome = await ProcessEntryAsync(pipeline, providerId, entry, hashStore, baseMetadata,
                options, progress, errors, cancellationToken).ConfigureAwait(false);
            if (outcome == EntryOutcome.Ingested)
                Interlocked.Increment(ref ingested);
            else
                Interlocked.Increment(ref skipped);
        }).ConfigureAwait(false);

        if (cleanupMode == CleanupMode.Full && hashStore is not null)
        {
            deleted = await CleanupDisappearedAsync(pipeline, providerId, hashStore, knownIds, seenIds,
                errors, cancellationToken).ConfigureAwait(false);
        }

        return new ProviderIngestionResult(ingested, skipped, deleted, errors.ToList());
    }

    /// <summary>
    /// Fail-fast validation of the caller-supplied options: one failure result up front
    /// instead of one identical <see cref="RagError.ValidationFailed"/> per document.
    /// </summary>
    private static RagError? ValidateOptions(IngestionOptions? options)
    {
        if (options is null)
            return null;

        var failures = new List<Models.ValidationFailure>();

        var validation = new IngestionOptionsValidator().Validate(options);
        if (!validation.IsValid)
        {
            foreach (ref readonly var failureRef in validation.Failures)
            {
                var failure = failureRef; // explicit copy: member access on 'ref readonly' would hide one
                failures.Add(new Models.ValidationFailure(failure.PropertyName, failure.ErrorMessage));
            }
        }

        // ParallelOptions accepts -1 as "unbounded" — preserve that; only 0 and < -1 are invalid.
        if (options.MaxDegreeOfParallelism == 0 || options.MaxDegreeOfParallelism < -1)
        {
            failures.Add(new Models.ValidationFailure(
                nameof(IngestionOptions.MaxDegreeOfParallelism),
                "MaxDegreeOfParallelism must be -1 (unbounded) or greater than 0."));
        }

        return failures.Count > 0 ? new RagError.ValidationFailed(failures) : null;
    }

    private static async Task<EntryOutcome> ProcessEntryAsync(
        IRagPipeline pipeline,
        ProviderId providerId,
        FileEntry entry,
        IContentHashStore? hashStore,
        DocumentMetadata? baseMetadata,
        IngestionOptions? options,
        IProgress<IngestionProgress>? progress,
        ConcurrentBag<RagError> errors,
        CancellationToken cancellationToken)
    {
        try
        {
            if (hashStore is not null && entry.ETag is not null)
            {
                var storedETag = await hashStore.GetETagAsync(providerId, entry.Id, cancellationToken).ConfigureAwait(false);
                if (string.Equals(entry.ETag, storedETag, StringComparison.Ordinal))
                    return EntryOutcome.Skipped;
            }

            var rawStream = await entry.OpenContentAsync(cancellationToken).ConfigureAwait(false);
            await using (rawStream.ConfigureAwait(false))
            {
                if (hashStore is null)
                {
                    var metadata = BuildMetadata(entry, baseMetadata, providerId);
                    var ingestResult = await pipeline.IngestAsync(rawStream, metadata, options, progress, cancellationToken).ConfigureAwait(false);
                    if (!ingestResult.IsSuccess)
                        throw new InvalidOperationException($"Ingestion failed: {ingestResult.Error}");
                    return EntryOutcome.Ingested;
                }

                return await IngestWithHashCheckAsync(pipeline, providerId, entry, hashStore, baseMetadata,
                    options, progress, rawStream, cancellationToken).ConfigureAwait(false);
            }
        }
        // ReservedMetadataKeyException is deliberately excluded alongside cancellation: it is a
        // connector authoring bug that repeats identically for every entry, so downgrading it to
        // a per-entry RagError would emit N copies of one bug and still ship a corrupted ranking.
        // Removing that exclusion silently reverts the escape — see the tests named at the throw
        // site in BuildMetadata.
        catch (Exception ex) when (ex is not OperationCanceledException and not ReservedMetadataKeyException)
        {
            errors.Add(new RagError.StorageFailed(ex));
            return EntryOutcome.Skipped;
        }
    }

    private static async Task<EntryOutcome> IngestWithHashCheckAsync(
        IRagPipeline pipeline,
        ProviderId providerId,
        FileEntry entry,
        IContentHashStore hashStore,
        DocumentMetadata? baseMetadata,
        IngestionOptions? options,
        IProgress<IngestionProgress>? progress,
        Stream rawStream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await rawStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var hash = ComputeHash(buffer.GetBuffer(), (int)buffer.Length);

        var storedHash = await hashStore.GetHashAsync(providerId, entry.Id, cancellationToken).ConfigureAwait(false);
        if (string.Equals(hash, storedHash, StringComparison.Ordinal))
        {
            // Only refresh ETag when there's a non-null ETag to store
            if (entry.ETag is not null)
                await hashStore.SetAsync(providerId, entry.Id, entry.ETag, hash, cancellationToken).ConfigureAwait(false);
            return EntryOutcome.Skipped;
        }

        buffer.Position = 0;
        var metadata = BuildMetadata(entry, baseMetadata, providerId);
        var ingestResult = await pipeline.IngestAsync(buffer, metadata, options, progress, cancellationToken).ConfigureAwait(false);
        if (!ingestResult.IsSuccess)
            throw new InvalidOperationException($"Ingestion failed: {ingestResult.Error}");
        await hashStore.SetAsync(providerId, entry.Id, entry.ETag, hash, cancellationToken).ConfigureAwait(false);
        return EntryOutcome.Ingested;
    }

    private static async Task<int> CleanupDisappearedAsync(
        IRagPipeline pipeline,
        ProviderId providerId,
        IContentHashStore hashStore,
        IReadOnlySet<EntryId> knownIds,
        ConcurrentDictionary<EntryId, byte> seenIds,
        ConcurrentBag<RagError> errors,
        CancellationToken cancellationToken)
    {
        var deleted = 0;
        foreach (var id in knownIds)
        {
            if (seenIds.ContainsKey(id)) continue;

            try
            {
                await pipeline.DeleteAsync(id.Value, cancellationToken).ConfigureAwait(false);
                await hashStore.RemoveAsync(providerId, id, cancellationToken).ConfigureAwait(false);
                deleted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add(new RagError.StorageFailed(ex));
            }
        }

        return deleted;
    }

    private static string ComputeHash(byte[] buffer, int length)
    {
        var hashBytes = SHA256.HashData(buffer.AsSpan(0, length));
        return Convert.ToHexString(hashBytes);
    }

    /// <summary>
    /// Merges base and entry metadata into <see cref="DocumentMetadata.Tags"/>, then writes
    /// <see cref="ReservedMetadataKeys.ProviderId"/>. Entry tags win over base tags on
    /// collision; <c>provider_id</c> wins over both.
    /// </summary>
    /// <exception cref="ReservedMetadataKeyException">
    /// An entry tag uses a key the framework writes itself (<see cref="ReservedMetadataKeys"/>).
    /// A connector tag does not lose to the framework value — <c>MetadataBehavior</c> applies
    /// connector tags first with <c>TryAdd</c>, so it <i>shadows</i> it. See the type's remarks
    /// for why this throws instead of yielding a per-entry failure.
    /// </exception>
    private static DocumentMetadata BuildMetadata(
        FileEntry entry, DocumentMetadata? baseMetadata, ProviderId providerId)
    {
        var tags = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);

        if (baseMetadata?.Tags is not null)
        {
            foreach (var (k, v) in baseMetadata.Tags)
                tags[k] = v;
        }

        if (entry.Metadata is not null)
        {
            foreach (var (k, v) in entry.Metadata)
            {
                // Before changing this throw — or the catch filter in ProcessEntryAsync that lets
                // it past, or the escape path through Parallel.ForEachAsync — note that two tests
                // pin the contract documented on IngestFromProviderAsync, and neither is obvious
                // from this line alone:
                //   IngestFromProviderAsync_ReservedKeyCollisionUnderParallelism_EscapesUnwrapped
                //     — callers receive this bare, not inside an AggregateException, even when
                //       several parallel workers throw at once.
                //   IngestFromProviderAsync_ReservedKeyCollision_LeavesAlreadyProcessedEntriesIngested
                //     — entries ingested before the collision surfaces stay ingested.
                // Both live in tests/Rag.NET.Tests/DataProviders/IngestFromProviderTests.cs.
                if (ReservedMetadataKeys.IsReserved(k))
                    throw new ReservedMetadataKeyException(k, providerId.Value, entry.Id.Value);
                tags[k] = v;
            }
        }

        // Written last, and reserved: every connector gains it without per-connector work, and
        // neither an entry tag (guarded above) nor a caller-supplied base tag can shadow it.
        tags[ReservedMetadataKeys.ProviderId] = providerId.Value;

        return new DocumentMetadata
        {
            DocumentId = new DocumentId(entry.Id.Value),
            FileName = entry.FileName,
            // Entry wins, like Tags and the timestamps: a provider yielding both a PDF and a
            // Markdown file needs to say so per entry, and a batch-level default cannot.
            ContentType = entry.ContentType ?? baseMetadata?.ContentType,
            // Entry-level timestamps are per-document and connector-set; baseMetadata's are a
            // batch-level default supplied once per IngestFromProviderAsync call — same
            // precedence as Tags just above, where the entry also wins on collision.
            CreatedAt = entry.CreatedAt ?? baseMetadata?.CreatedAt,
            UpdatedAt = entry.UpdatedAt ?? baseMetadata?.UpdatedAt,
            Tags = tags,
        };
    }
}
