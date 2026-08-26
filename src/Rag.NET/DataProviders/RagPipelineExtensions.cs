using System.Collections.Concurrent;
using System.Runtime.InteropServices;
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
    private enum EntryOutcome { Ingested, Skipped, Failed }

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
            // Nothing was attempted, so nothing failed either -- the error is the options themselves.
            return new ProviderIngestionResult([], [], [], [], [], [optionsError]);

        var errors = new ConcurrentBag<RagError>();

        IReadOnlySet<EntryId> knownIds = hashStore is not null && cleanupMode == CleanupMode.Full
            ? await hashStore.GetAllIdsAsync(providerId, cancellationToken).ConfigureAwait(false)
            : (IReadOnlySet<EntryId>)new HashSet<EntryId>();

        var seenIds = new ConcurrentDictionary<EntryId, byte>();

        // Collect entries first — IAsyncEnumerable cannot be iterated in parallel directly
        var entries = new List<FileEntry>();
        var listingFailed = false;
        await foreach (var result in provider.GetFilesAsync(cancellationToken).ConfigureAwait(false))
        {
            // Remembered, not just collected: a failed listing means an unknown number of entries
            // were never seen, which cleanup must not read as "they disappeared" (#400).
            if (result.IsFailure) { errors.Add(result.Error); listingFailed = true; continue; }
            entries.Add(result.Value);
        }

        var tally = await ProcessAllEntriesAsync(pipeline, providerId, entries, hashStore, baseMetadata,
            options, progress, errors, seenIds, cancellationToken).ConfigureAwait(false);

        var deleted = await CleanupIfRequestedAsync(pipeline, providerId, hashStore, cleanupMode,
            knownIds, seenIds, errors, BlockedBecause(tally.StoppedEarly, listingFailed),
            cancellationToken).ConfigureAwait(false);

        return new ProviderIngestionResult(tally.Ingested, tally.Skipped, tally.Failed,
            EntriesNeverReached(entries, seenIds), deleted, errors.ToList());
    }

    /// <summary>
    /// Why full cleanup must not run, when it must not.
    /// </summary>
    /// <remarks>
    /// Cleanup deletes what the run did not see. That is only safe when "not seen" reliably means
    /// "no longer at the provider" — so every way a run can fail to see an entry for some other
    /// reason has to be named here and block it.
    /// </remarks>
    private enum CleanupBlocked
    {
        /// <summary>The run saw the provider's entries in full; cleanup can proceed.</summary>
        None,

        /// <summary>
        /// <see cref="IngestionOptions.StopOnFirstError"/> ended the run early, so the entries
        /// after the failure were never visited.
        /// </summary>
        StoppedEarly,

        /// <summary>
        /// The provider failed to list one or more entries. Unlike <see cref="StoppedEarly"/>
        /// this is not recoverable by inspection: a failed listing carries no
        /// <see cref="EntryId"/>, so the run cannot even name what it missed.
        /// </summary>
        ListingFailed,
    }

    /// <summary>Picks the reason cleanup is blocked, if it is.</summary>
    /// <remarks>
    /// A listing failure outranks an early stop when both happened: it is the stronger statement,
    /// because an early stop at least leaves the unvisited entries nameable from the provider's
    /// own list, and a listing failure does not.
    /// </remarks>
    private static CleanupBlocked BlockedBecause(bool stoppedEarly, bool listingFailed) =>
        listingFailed ? CleanupBlocked.ListingFailed
        : stoppedEarly ? CleanupBlocked.StoppedEarly
        : CleanupBlocked.None;

    /// <summary>What one pass over the provider's entries produced.</summary>
    private sealed record EntryTally(
        IReadOnlyList<ProviderEntryOutcome> Ingested,
        IReadOnlyList<ProviderEntryOutcome> Skipped,
        IReadOnlyList<ProviderEntryOutcome> Failed,
        bool StoppedEarly);

    /// <summary>Reduces an entry to what a finished run can honestly say about it.</summary>
    /// <remarks>
    /// Drops <see cref="FileEntry.OpenContentAsync"/> deliberately: see
    /// <see cref="ProviderEntryOutcome"/>.
    /// </remarks>
    private static ProviderEntryOutcome Describe(FileEntry entry) =>
        new(entry.Id, entry.FileName, entry.ETag);

    /// <summary>
    /// The entries the loop never got to, when <see cref="IngestionOptions.StopOnFirstError"/>
    /// stopped it early.
    /// </summary>
    /// <remarks>
    /// Derived from <paramref name="seenIds"/> rather than counted during the loop: an entry is
    /// recorded there before it is processed, so one missing from it is one the loop stopped short
    /// of. Without this list a run that stopped early looks like a run that had less to do — the
    /// entries after the failure are in no other list.
    /// </remarks>
    private static List<ProviderEntryOutcome> EntriesNeverReached(
        List<FileEntry> entries,
        ConcurrentDictionary<EntryId, byte> seenIds)
    {
        var neverReached = new List<ProviderEntryOutcome>();
        foreach (ref readonly var entryRef in CollectionsMarshal.AsSpan(entries))
        {
            var entry = entryRef; // explicit copy: passing the 'ref readonly' along would hide one
            if (!seenIds.ContainsKey(entry.Id))
                neverReached.Add(Describe(entry));
        }

        return neverReached;
    }

    /// <summary>
    /// Processes every entry, stopping at the first failure when
    /// <see cref="IngestionOptions.StopOnFirstError"/> asks for it (#355).
    /// </summary>
    private static async Task<EntryTally> ProcessAllEntriesAsync(
        IRagPipeline pipeline,
        ProviderId providerId,
        List<FileEntry> entries,
        IContentHashStore? hashStore,
        DocumentMetadata? baseMetadata,
        IngestionOptions? options,
        IProgress<IngestionProgress>? progress,
        ConcurrentBag<RagError> errors,
        ConcurrentDictionary<EntryId, byte> seenIds,
        CancellationToken cancellationToken)
    {
        // Queues, not bags: under the default MaxDegreeOfParallelism of 1 this preserves the
        // order the provider listed the entries, which a bag would scramble for no reason.
        var ingested = new ConcurrentQueue<ProviderEntryOutcome>();
        var skipped = new ConcurrentQueue<ProviderEntryOutcome>();
        var failed = new ConcurrentQueue<ProviderEntryOutcome>();
        var stopOnFirstError = options?.StopOnFirstError ?? false;

        // Linked, so this method can stop the loop without that being mistaken for the caller
        // cancelling — the catch below tells the two apart.
        using var stopSignal = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = options?.MaxDegreeOfParallelism ?? 1,
            CancellationToken = stopSignal.Token,
        };

        try
        {
            await Parallel.ForEachAsync(entries, parallelOptions, async (entry, _) =>
            {
                seenIds.TryAdd(entry.Id, 0);
                var outcome = await ProcessEntryAsync(pipeline, providerId, entry, hashStore, baseMetadata,
                    options, progress, errors, cancellationToken).ConfigureAwait(false);
                // Explicit, not "Ingested else Skipped": that else-branch is what made a failure
                // indistinguishable from an up-to-date entry (#355), and would swallow Failed too.
                switch (outcome)
                {
                    case EntryOutcome.Ingested: ingested.Enqueue(Describe(entry)); break;
                    case EntryOutcome.Failed:
                        failed.Enqueue(Describe(entry));
                        if (stopOnFirstError)
                            await stopSignal.CancelAsync().ConfigureAwait(false);
                        break;
                    default: skipped.Enqueue(Describe(entry)); break;
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopSignal.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // This method's own stop, not the caller's. Everything counted so far is the result; a
            // caller cancellation does not match this filter and still propagates.
        }

        return new EntryTally([.. ingested], [.. skipped], [.. failed],
            stopSignal.IsCancellationRequested && !cancellationToken.IsCancellationRequested);
    }

    /// <summary>
    /// Runs full cleanup when it was asked for and can actually work, and says so when it cannot.
    /// </summary>
    /// <remarks>
    /// <b>Asking for <see cref="CleanupMode.Full"/> without a hash store used to do nothing,
    /// quietly.</b> Cleanup removes what this run did not see by comparing against the ids the
    /// store recorded on earlier runs; with no store there is no history, every document looks new,
    /// and nothing is ever deleted. Reported as #394 — pages excluded from a sitemap after indexing
    /// stayed in the index and the run reported success.
    /// </remarks>
    private static async Task<List<ProviderEntryOutcome>> CleanupIfRequestedAsync(
        IRagPipeline pipeline,
        ProviderId providerId,
        IContentHashStore? hashStore,
        CleanupMode cleanupMode,
        IReadOnlySet<EntryId> knownIds,
        ConcurrentDictionary<EntryId, byte> seenIds,
        ConcurrentBag<RagError> errors,
        CleanupBlocked blocked,
        CancellationToken cancellationToken)
    {
        if (cleanupMode != CleanupMode.Full)
        {
            return [];
        }

        if (hashStore is null)
        {
            errors.Add(new RagError.ValidationFailed(
            [
                new ValidationFailure(
                    nameof(cleanupMode),
                    "CleanupMode.Full was requested without a hashStore, so nothing was deleted. " +
                    "Cleanup removes documents this run did not see by comparing against the ids the " +
                    "store recorded on previous runs; with no store there is nothing to compare. Pass " +
                    "an IContentHashStore — the same one the earlier runs used, or cleanup has no " +
                    "history to work from."),
            ]));
            return [];
        }

        return await CleanupUnlessEntriesWereMissedAsync(pipeline, providerId, hashStore,
            knownIds, seenIds, errors, blocked, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs full cleanup, unless the run stopped at its first error — in which case it must not.
    /// </summary>
    /// <remarks>
    /// Cleanup decides what disappeared by comparing what the store knows against what this run
    /// saw. A run that stopped early never visited the rest of the provider's entries, so cleanup
    /// would read every unvisited document as disappeared and delete it. Skipping is the only safe
    /// answer, and it is reported rather than done quietly: a caller who asked for
    /// <see cref="CleanupMode.Full"/> and got no deletions is owed the reason.
    /// </remarks>
    private static async Task<List<ProviderEntryOutcome>> CleanupUnlessEntriesWereMissedAsync(
        IRagPipeline pipeline,
        ProviderId providerId,
        IContentHashStore hashStore,
        IReadOnlySet<EntryId> knownIds,
        ConcurrentDictionary<EntryId, byte> seenIds,
        ConcurrentBag<RagError> errors,
        CleanupBlocked blocked,
        CancellationToken cancellationToken)
    {
        if (blocked == CleanupBlocked.None)
        {
            return await CleanupDisappearedAsync(pipeline, providerId, hashStore, knownIds, seenIds,
                errors, cancellationToken).ConfigureAwait(false);
        }

        errors.Add(new RagError.ValidationFailed([DescribeBlockedCleanup(blocked)]));
        return [];
    }

    /// <summary>Says which entries the run failed to see, and why that stops cleanup.</summary>
    private static ValidationFailure DescribeBlockedCleanup(CleanupBlocked blocked) => blocked switch
    {
        CleanupBlocked.StoppedEarly => new ValidationFailure(
            nameof(IngestionOptions.StopOnFirstError),
            "Full cleanup was skipped because ingestion stopped at the first error. The entries "
            + "after the failure were never visited, so deleting everything this run did not see "
            + "would remove documents still present at the provider. Fix the failure and re-run "
            + "to clean up."),

        CleanupBlocked.ListingFailed => new ValidationFailure(
            nameof(IFileContentProvider.GetFilesAsync),
            "Full cleanup was skipped because the provider failed to list one or more entries. A "
            + "failed listing carries no entry id, so this run cannot tell which documents it did "
            + "not see — and deleting everything unseen would remove documents that are still "
            + "there. One failed page of a sitemap is enough to lose the rest. The listing errors "
            + "are in Errors; fix them and re-run to clean up."),

        _ => throw new ArgumentOutOfRangeException(nameof(blocked), blocked, "Unhandled reason."),
    };


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

            // Failed, not Skipped. Skipped means the entry was already up to date; saying
            // that about a throw reported a broken run as a quiet one (#355).
            return EntryOutcome.Failed;
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

    private static async Task<List<ProviderEntryOutcome>> CleanupDisappearedAsync(
        IRagPipeline pipeline,
        ProviderId providerId,
        IContentHashStore hashStore,
        IReadOnlySet<EntryId> knownIds,
        ConcurrentDictionary<EntryId, byte> seenIds,
        ConcurrentBag<RagError> errors,
        CancellationToken cancellationToken)
    {
        var deleted = new List<ProviderEntryOutcome>();
        foreach (var id in knownIds)
        {
            if (seenIds.ContainsKey(id)) continue;

            try
            {
                await pipeline.DeleteAsync(id.Value, cancellationToken).ConfigureAwait(false);
                await hashStore.RemoveAsync(providerId, id, cancellationToken).ConfigureAwait(false);
                // Id only: nothing listed this document this run, so there is no name to report.
                deleted.Add(new ProviderEntryOutcome(id));
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
