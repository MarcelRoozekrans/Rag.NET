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
                    var metadata = BuildMetadata(entry, baseMetadata);
                    var ingestResult = await pipeline.IngestAsync(rawStream, metadata, options, progress, cancellationToken).ConfigureAwait(false);
                    if (!ingestResult.IsSuccess)
                        throw new InvalidOperationException($"Ingestion failed: {ingestResult.Error}");
                    return EntryOutcome.Ingested;
                }

                return await IngestWithHashCheckAsync(pipeline, providerId, entry, hashStore, baseMetadata,
                    options, progress, rawStream, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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
        var metadata = BuildMetadata(entry, baseMetadata);
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

    private static DocumentMetadata BuildMetadata(FileEntry entry, DocumentMetadata? baseMetadata)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);

        if (baseMetadata?.Tags is not null)
        {
            foreach (var (k, v) in baseMetadata.Tags)
                tags[k] = v;
        }

        if (entry.Metadata is not null)
        {
            foreach (var (k, v) in entry.Metadata)
                tags[k] = v;
        }

        return new DocumentMetadata
        {
            DocumentId = new DocumentId(entry.Id.Value),
            FileName = entry.FileName,
            ContentType = baseMetadata?.ContentType,
            Tags = tags,
        };
    }
}
